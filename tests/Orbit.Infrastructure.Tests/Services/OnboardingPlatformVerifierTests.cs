using FluentAssertions;
using Google.Apis.PlayIntegrity.v1.Data;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class OnboardingPlatformVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static OnboardingSecuritySettings Settings() => new()
    {
        AndroidPackageName = "org.useorbit.app",
        AndroidCertificateDigests = ["configured-signing-digest"],
        TurnstileHostnames = ["example.com"]
    };

    private static TokenPayloadExternal Payload() => new()
    {
        RequestDetails = new RequestDetails { RequestPackageName = "org.useorbit.app", RequestHash = "bound-request-hash", TimestampMillis = Now.ToUnixTimeMilliseconds() },
        AppIntegrity = new AppIntegrity { PackageName = "org.useorbit.app", AppRecognitionVerdict = "PLAY_RECOGNIZED", CertificateSha256Digest = ["configured-signing-digest"] },
        DeviceIntegrity = new DeviceIntegrity { DeviceRecognitionVerdict = ["MEETS_DEVICE_INTEGRITY"] },
        AccountDetails = new AccountDetails { AppLicensingVerdict = "LICENSED" }
    };

    [Fact]
    public void PlayIntegrity_BindsRequestPackageHashFreshnessAndVerdicts()
        => PlayIntegrityOnboardingVerifier.IsValid(Payload(), Settings(), "bound-request-hash", Now).Should().BeTrue();

    [Theory]
    [InlineData("request-package")]
    [InlineData("request-hash")]
    [InlineData("old")]
    [InlineData("future")]
    [InlineData("app")]
    [InlineData("package")]
    [InlineData("signing-certificate")]
    [InlineData("device")]
    [InlineData("license")]
    [InlineData("test")]
    public void PlayIntegrity_RejectsAnyUntrustedSignal(string defect)
    {
        var payload = Payload();
        switch (defect)
        {
            case "request-package": payload.RequestDetails.RequestPackageName = "other.app"; break;
            case "request-hash": payload.RequestDetails.RequestHash = "replayed-hash"; break;
            case "old": payload.RequestDetails.TimestampMillis = Now.AddMinutes(-6).ToUnixTimeMilliseconds(); break;
            case "future": payload.RequestDetails.TimestampMillis = Now.AddSeconds(1).ToUnixTimeMilliseconds(); break;
            case "app": payload.AppIntegrity.AppRecognitionVerdict = "UNRECOGNIZED_VERSION"; break;
            case "package": payload.AppIntegrity.PackageName = "other.app"; break;
            case "signing-certificate": payload.AppIntegrity.CertificateSha256Digest = ["unknown-digest"]; break;
            case "device": payload.DeviceIntegrity.DeviceRecognitionVerdict = ["MEETS_BASIC_INTEGRITY"]; break;
            case "license": payload.AccountDetails.AppLicensingVerdict = "UNLICENSED"; break;
            case "test": payload.TestingDetails = new TestingDetails { IsTestingResponse = true }; break;
        }
        PlayIntegrityOnboardingVerifier.IsValid(payload, Settings(), "bound-request-hash", Now).Should().BeFalse();
    }

    [Theory]
    [InlineData("web")]
    [InlineData("desktop")]
    public async Task Turnstile_IsMandatoryEvenWhenBotSwitchIsOff(string platform)
    {
        var turnstile = Substitute.For<ITurnstileVerificationService>();
        turnstile.VerifyAsync("valid-proof", Arg.Any<CancellationToken>())
            .Returns(new TurnstileVerificationResult(true, [], "example.com"));
        var verifier = new TurnstileOnboardingVerifier(turnstile,
            Options.Create(new BotProtectionSettings { Enabled = false, SecretKey = "production-secret" }), Options.Create(Settings()));
        (await verifier.VerifyAsync(new OnboardingAttestation(platform, "valid-proof"), "challenge", null, 0, default)).Should().NotBeNull();
        await turnstile.Received(1).VerifyAsync("valid-proof", Arg.Any<CancellationToken>());
        turnstile.VerifyAsync("valid-proof", Arg.Any<CancellationToken>())
            .Returns(new TurnstileVerificationResult(true, [], "untrusted.example"));
        (await verifier.VerifyAsync(new OnboardingAttestation(platform, "valid-proof"), "challenge", null, 0, default)).Should().BeNull();
    }

    [Fact]
    public async Task EveryUnconfiguredPlatform_FailsClosed()
    {
        var options = Options.Create(new OnboardingSecuritySettings());
        IOnboardingAttestationVerifier[] verifiers =
        [
            new TurnstileOnboardingVerifier(Substitute.For<ITurnstileVerificationService>(), Options.Create(new BotProtectionSettings()), options),
            new PlayIntegrityOnboardingVerifier(options, TimeProvider.System),
            new AppAttestOnboardingVerifier(options, TimeProvider.System)
        ];
        foreach (var platform in new[] { "web", "desktop", "android", "ios" })
        {
            var verifier = verifiers.Single(verifier => verifier.Supports(platform));
            verifier.IsConfigured.Should().BeFalse();
            (await verifier.VerifyAsync(new OnboardingAttestation(platform, "invalid"), "challenge", null, 0, default)).Should().BeNull();
        }
    }
}
