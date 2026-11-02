using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class OnboardingAccessTests : IDisposable
{
    private readonly OrbitDbContext db = new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly TestClock clock = new();
    private readonly IOnboardingAttestationVerifier verifier = Substitute.For<IOnboardingAttestationVerifier>();
    private readonly IAppConfigService config = Substitute.For<IAppConfigService>();
    private readonly OnboardingSecuritySettings settings = new() { SigningSecret = new string('s', 64) };
    private const string Ip = "192.0.2.1";
    private static readonly OnboardingAttestation Proof = new("web", "valid-token");
    private static readonly OnboardingInput Input = OnboardingInput.Create("Read every day", "en").Value;

    public OnboardingAccessTests()
    {
        config.GetAsync(AppConfigKeys.OnboardingAttestationCallsPerHour, 0, Arg.Any<CancellationToken>()).Returns(1000);
        config.GetAsync(AppConfigKeys.OnboardingAttestationCallsPerDay, 0, Arg.Any<CancellationToken>()).Returns(10000);
        config.GetAsync(AppConfigKeys.OnboardingEnrollAttemptsPerInstallPerHour, 0, Arg.Any<CancellationToken>()).Returns(3);
        verifier.Supports("web").Returns(true);
        verifier.IsConfigured.Returns(true);
        verifier.VerifyAsync(Arg.Any<OnboardingAttestation>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<uint>(), Arg.Any<CancellationToken>()).Returns(new VerifiedOnboardingAttestation(null, 0));
        config.GetAsync(OnboardingAccessService.IpHourlyLimit, 0, Arg.Any<CancellationToken>()).Returns(10);
        config.GetAsync(OnboardingAccessService.InstallHourlyLimit, 0, Arg.Any<CancellationToken>()).Returns(20);
        config.GetAsync(OnboardingAccessService.DailyCallLimit, 0, Arg.Any<CancellationToken>()).Returns(100);
        config.GetAsync(OnboardingAccessService.DailyTokenLimit, 0, Arg.Any<CancellationToken>()).Returns(1_000_000);
    }

    private OnboardingAccessService Service(IAppConfigService? appConfig = null)
    {
        var limiter = new DistributedRateLimitService(db, clock);
        return new(Options.Create(settings), [verifier], limiter, limiter, appConfig ?? config, clock);
    }

    private async Task<string> Enroll(string clientIp = Ip)
    {
        var challenge = await Service().ChallengeAsync("web", clientIp, default);
        challenge.Should().NotBeNull();
        var install = await Service().EnrollAsync(challenge!.Challenge, Proof, clientIp, default);
        install.Should().NotBeNull();
        return install!.InstallId;
    }

    [Fact]
    public async Task SignedInstall_RotatesOnce_RejectsForgedReplayedAndCrossIpCredentials()
    {
        var install = await Enroll();
        (await Service().AuthorizeAsync(install + "forged", Proof, Input, Ip, default)).Should().BeNull();
        (await Service().AuthorizeAsync(install, Proof, Input, "192.0.2.2", default)).Should().BeNull();
        var result = await Service().AuthorizeAsync(install, Proof, Input, Ip, default);
        result.Should().NotBeNull();
        result!.InstallId.Should().NotBe(install);
        (await Service().AuthorizeAsync(install, Proof, Input, Ip, default)).Should().BeNull();
        (await Service().AuthorizeAsync(result.InstallId, Proof, Input, Ip, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task EquivalentBase64Credential_CannotBypassReplayTracking()
    {
        var install = await Enroll();
        (await Service().AuthorizeAsync(install, Proof, Input, Ip, default)).Should().NotBeNull();
        var parts = install.Split('.');
        var alternate = $"{parts[0]}. {parts[1]}   ";
        verifier.ClearReceivedCalls();

        (await Service().AuthorizeAsync(alternate, Proof, Input, Ip, default)).Should().BeNull();
        verifier.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "VerifyAsync");
    }

    [Fact]
    public async Task ExpiredCredential_IsRejectedBeforeVerification()
    {
        var install = await Enroll();
        verifier.ClearReceivedCalls();
        clock.Now = clock.Now.AddHours(2);
        (await Service().AuthorizeAsync(install, Proof, Input, Ip, default)).Should().BeNull();
        verifier.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "VerifyAsync");
    }

    [Fact]
    public async Task EleventhCallFromIp_IsLimitedAcrossInstallIds()
    {
        var install = await Enroll();
        for (var count = 0; count < 10; count++)
            install = (await Service().AuthorizeAsync(install, Proof, Input, Ip, default))!.InstallId;
        var secondInstall = await Enroll();
        (await Service().AuthorizeAsync(secondInstall, Proof, Input, Ip, default)).Should().BeNull();
        db.DistributedRateLimitBuckets.Single(bucket => bucket.PolicyName == "onboarding-ip").Count.Should().Be(10);
    }

    [Theory]
    [InlineData(OnboardingAccessService.IpHourlyLimit, 1)]
    [InlineData(OnboardingAccessService.InstallHourlyLimit, 1)]
    [InlineData(OnboardingAccessService.DailyCallLimit, 1)]
    [InlineData(OnboardingAccessService.DailyTokenLimit, OnboardingAccessService.ReservedTokensPerCall)]
    public async Task EachQuota_RefusesTheNextCall(string key, int limit)
    {
        config.GetAsync(key, 0, Arg.Any<CancellationToken>()).Returns(limit);
        var install = await Enroll();
        var first = await Service().AuthorizeAsync(install, Proof, Input, Ip, default);
        first.Should().NotBeNull();
        (await Service().AuthorizeAsync(first!.InstallId, Proof, Input, Ip, default)).Should().BeNull();
    }

    [Theory]
    [InlineData(OnboardingAccessService.DailyCallLimit, 1)]
    [InlineData(OnboardingAccessService.DailyTokenLimit, OnboardingAccessService.ReservedTokensPerCall)]
    public async Task DailyBudgets_AreSharedAcrossIpAndInstallIds(string key, int limit)
    {
        config.GetAsync(key, 0, Arg.Any<CancellationToken>()).Returns(limit);
        (await Service().AuthorizeAsync(await Enroll(), Proof, Input, Ip, default)).Should().NotBeNull();
        const string otherIp = "192.0.2.2";
        (await Service().AuthorizeAsync(await Enroll(otherIp), Proof, Input, otherIp, default)).Should().BeNull();
    }

    [Fact]
    public async Task InvalidProofOnSetup_ConsumesCredentialWithoutReservingModelBudgets()
    {
        var install = await Enroll();
        verifier.VerifyAsync(Arg.Any<OnboardingAttestation>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<uint>(),
            Arg.Any<CancellationToken>()).Returns((VerifiedOnboardingAttestation?)null);

        (await Service().AuthorizeAsync(install, Proof, Input, Ip, default)).Should().BeNull();
        db.DistributedRateLimitBuckets.Should().NotContain(bucket => bucket.PolicyName == "onboarding-calls"
            || bucket.PolicyName == "onboarding-tokens");
        verifier.ClearReceivedCalls();
        (await Service().AuthorizeAsync(install, Proof, Input, Ip, default)).Should().BeNull();
        verifier.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "VerifyAsync");
    }

    [Theory]
    [InlineData(OnboardingAccessService.IpHourlyLimit)]
    [InlineData(OnboardingAccessService.InstallHourlyLimit)]
    [InlineData(OnboardingAccessService.DailyCallLimit)]
    [InlineData(OnboardingAccessService.DailyTokenLimit)]
    public async Task MissingQuota_FailsClosed(string key)
    {
        config.GetAsync(key, 0, Arg.Any<CancellationToken>()).Returns(0);
        (await Service().AuthorizeAsync(await Enroll(), Proof, Input, Ip, default)).Should().BeNull();
    }

    [Fact]
    public async Task UnconfiguredAttestationOrSecret_NeverIssuesAnInstall()
    {
        verifier.IsConfigured.Returns(false);
        (await Service().ChallengeAsync("web", Ip, default)).Should().BeNull();
        verifier.IsConfigured.Returns(true);
        settings.SigningSecret = "";
        (await Service().ChallengeAsync("web", Ip, default)).Should().BeNull();
        db.DistributedRateLimitBuckets.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidAttestation_RefusesEnrollmentAndChallengeReplay()
    {
        var challenge = (await Service().ChallengeAsync("web", Ip, default))!.Challenge;
        verifier.VerifyAsync(Arg.Any<OnboardingAttestation>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<uint>(),
            Arg.Any<CancellationToken>()).Returns((VerifiedOnboardingAttestation?)null);
        (await Service().EnrollAsync(challenge, Proof, Ip, default)).Should().BeNull();
        verifier.ClearReceivedCalls();
        (await Service().EnrollAsync(challenge, Proof, Ip, default)).Should().BeNull();
        verifier.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "VerifyAsync");
    }

    [Fact]
    public async Task OnlyBucketsAreStored_WithNoPromptResponseIpOrInstallInClear()
    {
        var install = await Enroll();
        (await Service().AuthorizeAsync(install, Proof, Input, Ip, default)).Should().NotBeNull();
        db.ChangeTracker.Entries().Should().OnlyContain(entry => entry.Entity is DistributedRateLimitBucket);
        var stored = System.Text.Json.JsonSerializer.Serialize(db.DistributedRateLimitBuckets.ToArray());
        stored.Should().NotContain(Input.Title).And.NotContain(Ip).And.NotContain(install).And.NotContain("07:00");
    }

    [Theory]
    [InlineData("OnboardingAttestationCallsPerHour")]
    [InlineData("OnboardingAttestationCallsPerDay")]
    public async Task EnrollmentGlobalQuota_StopsBeforeVerifier(string key)
    {
        var challenge = (await Service().ChallengeAsync("web", Ip, default))!.Challenge;
        config.GetAsync(key, 0, Arg.Any<CancellationToken>()).Returns(1);
        var quota = new DistributedRateLimitService(db, clock);
        var policy = key.EndsWith("Hour", StringComparison.Ordinal) ? "onboarding-attestation-hour" : "onboarding-attestation-day";
        var window = key.EndsWith("Hour", StringComparison.Ordinal) ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        await quota.TryAcquireAsync(policy, "global", 1, 1, window, default);
        verifier.ClearReceivedCalls();
        (await Service().EnrollAsync(challenge, Proof, Ip, default)).Should().BeNull();
        verifier.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "VerifyAsync");
    }

    [Theory]
    [InlineData("OnboardingAttestationCallsPerHour")]
    [InlineData("OnboardingAttestationCallsPerDay")]
    public async Task ChallengeGlobalQuota_IsSharedAcrossIps(string key)
    {
        config.GetAsync(key, 0, Arg.Any<CancellationToken>()).Returns(1);
        (await Service().ChallengeAsync("web", Ip, default)).Should().NotBeNull();
        (await Service().ChallengeAsync("web", "192.0.2.2", default)).Should().BeNull();
    }

    [Fact]
    public async Task EnrollmentAttempts_AreCappedPerSignedSubjectEvenOnReplay()
    {
        config.GetAsync(AppConfigKeys.OnboardingEnrollAttemptsPerInstallPerHour, 0, Arg.Any<CancellationToken>()).Returns(2);
        var challenge = (await Service().ChallengeAsync("web", Ip, default))!.Challenge;
        (await Service().EnrollAsync(challenge, Proof, Ip, default)).Should().NotBeNull();
        (await Service().EnrollAsync(challenge, Proof, Ip, default)).Should().BeNull();
        (await Service().EnrollAsync(challenge, Proof, Ip, default)).Should().BeNull();
        db.DistributedRateLimitBuckets.Single(b => b.PolicyName == "onboarding-enroll-install").Count.Should().Be(2);
        verifier.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "VerifyAsync").Should().Be(1);
    }

    [Fact]
    public async Task OverLimitChallenges_DoNotSpendGlobalAdmission()
    {
        config.GetAsync(AppConfigKeys.OnboardingAttestationCallsPerHour, 0, Arg.Any<CancellationToken>()).Returns(14);
        config.GetAsync(AppConfigKeys.OnboardingAttestationCallsPerDay, 0, Arg.Any<CancellationToken>()).Returns(14);
        for (var count = 0; count < 10; count++)
            (await Service().ChallengeAsync("web", Ip, default)).Should().NotBeNull();
        for (var count = 0; count < 40; count++)
            (await Service().ChallengeAsync("web", Ip, default)).Should().BeNull();

        AssertAttestationCounts(10);
        (await Enroll("192.0.2.2")).Should().NotBeNullOrEmpty();
        AssertAttestationCounts(12);
        verifier.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "VerifyAsync").Should().Be(1);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(100)]
    public async Task ReplayedChallenges_DoNotSpendGlobalAdmission(int installAttemptLimit)
    {
        config.GetAsync(AppConfigKeys.OnboardingEnrollAttemptsPerInstallPerHour, 0, Arg.Any<CancellationToken>())
            .Returns(installAttemptLimit);
        config.GetAsync(AppConfigKeys.OnboardingAttestationCallsPerHour, 0, Arg.Any<CancellationToken>()).Returns(6);
        config.GetAsync(AppConfigKeys.OnboardingAttestationCallsPerDay, 0, Arg.Any<CancellationToken>()).Returns(6);
        var challenge = (await Service().ChallengeAsync("web", Ip, default))!.Challenge;
        (await Service().EnrollAsync(challenge, Proof, Ip, default)).Should().NotBeNull();
        for (var count = 0; count < 40; count++)
            (await Service().EnrollAsync(challenge, Proof, Ip, default)).Should().BeNull();

        AssertAttestationCounts(2);
        (await Enroll("192.0.2.2")).Should().NotBeNullOrEmpty();
        AssertAttestationCounts(4);
        verifier.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "VerifyAsync").Should().Be(2);
    }

    private void AssertAttestationCounts(int expected)
    {
        foreach (var policy in new[] { "onboarding-attestation-hour", "onboarding-attestation-day" })
            db.DistributedRateLimitBuckets.Single(bucket => bucket.PolicyName == policy).Count.Should().Be(expected);
    }

    [Fact]
    public async Task GlobalHourlyQuota_ResetsWhileDailyQuotaStillApplies()
    {
        config.GetAsync(AppConfigKeys.OnboardingAttestationCallsPerHour, 0, Arg.Any<CancellationToken>()).Returns(1);
        config.GetAsync(AppConfigKeys.OnboardingAttestationCallsPerDay, 0, Arg.Any<CancellationToken>()).Returns(2);
        (await Service().ChallengeAsync("web", Ip, default)).Should().NotBeNull();
        (await Service().ChallengeAsync("web", "192.0.2.2", default)).Should().BeNull();
        clock.Now = clock.Now.AddHours(1);
        (await Service().ChallengeAsync("web", "192.0.2.2", default)).Should().NotBeNull();
        clock.Now = clock.Now.AddHours(1);
        (await Service().ChallengeAsync("web", "192.0.2.3", default)).Should().BeNull();
    }

    public void Dispose() => db.Dispose();
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 12, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
