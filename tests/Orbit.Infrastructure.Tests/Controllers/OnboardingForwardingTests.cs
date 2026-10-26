using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Extensions;
using Orbit.Api.Middleware;
using Orbit.Application.Common;
using Orbit.Application.Onboarding.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Controllers;

public sealed class OnboardingForwardingTests : IAsyncLifetime
{
    private const string Secret = "test-forwarding-secret";
    private const string Egress = "203.0.113.7";
    private const string Visitor = "198.51.100.1";
    private const string OtherVisitor = "198.51.100.2";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly OrbitDbContext db = new(new DbContextOptionsBuilder<OrbitDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly IOnboardingAttestationVerifier verifier = Substitute.For<IOnboardingAttestationVerifier>();
    private readonly IOnboardingSuggestionService suggestions = Substitute.For<IOnboardingSuggestionService>();
    private readonly WebApplication application;
    private readonly RequestDelegate pipeline;

    public OnboardingForwardingTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Auth:BffForwardingSecret"] = Secret;
        builder.Configuration["OnboardingSecurity:AllowedOrigins:0"] = "https://app.example";
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddApiPipeline(builder);
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddCorsPolicies(builder);
        builder.AddOrbitRateLimiting();
        builder.Services.AddControllers().AddApplicationPart(typeof(OnboardingController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Substitute.For<IGenericRepository<User>>());
        builder.Services.AddMediatR(options => options.RegisterServicesFromAssemblyContaining<GetOnboardingChallengeCommand>());
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddScoped<IDistributedQuotaService, DistributedRateLimitService>();
        builder.Services.AddScoped<IOnboardingAccessService, OnboardingAccessService>();
        builder.Services.AddSingleton(Options.Create(new OnboardingSecuritySettings { SigningSecret = new string('s', 64) }));

        verifier.Supports(Arg.Any<string>()).Returns(true);
        verifier.IsConfigured.Returns(true);
        verifier.VerifyAsync(Arg.Any<OnboardingAttestation>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<uint>(), Arg.Any<CancellationToken>()).Returns(new VerifiedOnboardingAttestation(null, 0));
        builder.Services.AddSingleton(verifier);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(OnboardingAccessService.IpHourlyLimit, 0, Arg.Any<CancellationToken>()).Returns(10);
        config.GetAsync(OnboardingAccessService.InstallHourlyLimit, 0, Arg.Any<CancellationToken>()).Returns(20);
        config.GetAsync(OnboardingAccessService.DailyCallLimit, 0, Arg.Any<CancellationToken>()).Returns(100);
        config.GetAsync(OnboardingAccessService.DailyTokenLimit, 0, Arg.Any<CancellationToken>()).Returns(1_000_000);
        builder.Services.AddSingleton(config);
        var moderation = Substitute.For<IContentModerationService>();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ModerationResult(false, false, []));
        builder.Services.AddSingleton(moderation);
        suggestions.SuggestAsync(Arg.Any<OnboardingInput>(), Arg.Any<CancellationToken>())
            .Returns(new HabitSetupSuggestion(null, FrequencyUnit.Day, 1, [], false, null, "07:00", [], []));
        builder.Services.AddSingleton(suggestions);

        application = builder.Build();
        var routes = new ApplicationBuilder(application.Services);
        routes.UseRouting();
        routes.UseMiddleware<AnonymousOnboardingMiddleware>();
        routes.UseCors();
        routes.UseAuthorization();
        routes.UseEndpoints(endpoints => endpoints.MapControllers());
        pipeline = routes.Build();
    }

    [Fact]
    public async Task VerifiedVisitors_HaveIndependentCredentialsAndHourlyBuckets()
    {
        var first = Forwarded(Visitor);
        var second = Forwarded(OtherVisitor);
        var firstChallenge = await ChallengeAsync(first);
        var secondChallenge = await ChallengeAsync(second);
        AssertLimited(await EnrollAsync(firstChallenge, second));
        var firstInstall = Read<OnboardingInstall>(await EnrollAsync(firstChallenge, first)).InstallId;
        var secondInstall = Read<OnboardingInstall>(await EnrollAsync(secondChallenge, second)).InstallId;
        AssertLimited(await SetupAsync(firstInstall, second));

        for (var call = 0; call < 10; call++)
        {
            firstInstall = RotatedInstall(await SetupAsync(firstInstall, first), firstInstall);
            secondInstall = RotatedInstall(await SetupAsync(secondInstall, second), secondInstall);
        }
        AssertLimited(await SetupAsync(firstInstall, first));
        AssertLimited(await SetupAsync(secondInstall, second));
        var buckets = await db.DistributedRateLimitBuckets.Where(bucket => bucket.PolicyName == "onboarding-ip").ToListAsync();
        buckets.Should().HaveCount(2).And.OnlyContain(bucket => bucket.Count == 10);
        buckets.Select(bucket => bucket.PartitionKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task VerifiedVisitors_HaveIndependentChallengeCeilings()
    {
        foreach (var visitor in new[] { Visitor, OtherVisitor })
        {
            for (var attempt = 0; attempt < 10; attempt++)
                await ChallengeAsync(Forwarded(visitor));
            AssertLimited(await SendAsync("challenge", new OnboardingController.ChallengeRequest("web"), Forwarded(visitor)));
        }
        var buckets = await db.DistributedRateLimitBuckets.Where(bucket => bucket.PolicyName == "onboarding-enroll").ToListAsync();
        buckets.Should().HaveCount(2).And.OnlyContain(bucket => bucket.Count == 10);
    }

    [Fact]
    public async Task StableVisitor_RetainsBindingAndRotationAcrossEgressConnections()
    {
        var challenge = await ChallengeAsync(Forwarded(Visitor));
        var enrollment = await EnrollAsync(challenge, Forwarded(Visitor, "203.0.113.8"));
        var install = Read<OnboardingInstall>(enrollment).InstallId;
        AssertLimited(await EnrollAsync(challenge, Forwarded(Visitor, "203.0.113.9")));
        var rotated = RotatedInstall(await SetupAsync(install, Forwarded(Visitor, "203.0.113.9")), install);
        AssertLimited(await SetupAsync(install, Forwarded(Visitor)));
        RotatedInstall(await SetupAsync(rotated, Forwarded(Visitor, "203.0.113.10")), rotated);
        db.DistributedRateLimitBuckets.Count(bucket => bucket.PolicyName == "onboarding-ip").Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("forged")]
    [InlineData("repeated")]
    [InlineData("comma-separated")]
    public async Task UnverifiedForwarding_UsesConnectionBindingAndCannotRotatePastQuotas(string? secret)
    {
        string[]? secrets = secret switch
        {
            null => null,
            "repeated" => [Secret, Secret],
            "comma-separated" => [$"{Secret}, {Secret}"],
            _ => [secret]
        };
        var client = new Client(Egress, [Visitor], secrets);
        var challenge = await ChallengeAsync(client);
        var install = Read<OnboardingInstall>(await EnrollAsync(challenge, client with { VisitorIps = [OtherVisitor] })).InstallId;
        AssertLimited(await SetupAsync(install, client with { ConnectionIp = "203.0.113.8" }));
        for (var call = 0; call < 10; call++)
            install = RotatedInstall(await SetupAsync(install, client with { VisitorIps = [$"198.51.100.{call + 10}"] }), install);
        AssertLimited(await SetupAsync(install, client with { VisitorIps = [OtherVisitor] }));
        db.DistributedRateLimitBuckets.Count(bucket => bucket.PolicyName == "onboarding-ip").Should().Be(1);
    }

    public static IEnumerable<object?[]> InvalidForwardingRequests()
    {
        foreach (var endpoint in new[] { "challenge", "install", "suggest-setup" })
            foreach (var address in new[] { null, "", "invalid", "198.51.100.1, 198.51.100.2", "repeated" })
                yield return [endpoint, address];
    }

    [Theory]
    [MemberData(nameof(InvalidForwardingRequests))]
    public async Task InvalidForwardedVisitor_IsRejectedWithoutConsumingCredentialsOrQuotas(string endpoint, string? address)
    {
        var direct = new Client(Egress, null, null);
        var challenge = await ChallengeAsync(direct);
        var install = Read<OnboardingInstall>(await EnrollAsync(await ChallengeAsync(direct), direct)).InstallId;
        object body = endpoint switch
        {
            "challenge" => new OnboardingController.ChallengeRequest("web"),
            "install" => new OnboardingController.EnrollmentRequest(challenge, Proof("web")),
            _ => new OnboardingController.SuggestSetupRequest("Read", "en", Proof("web"))
        };
        string[]? addresses = address switch { null => null, "repeated" => [Visitor, OtherVisitor], _ => [address] };
        var before = await db.DistributedRateLimitBuckets.SumAsync(bucket => bucket.Count);
        verifier.ClearReceivedCalls();
        AssertLimited(await SendAsync(endpoint, body, new Client(Egress, addresses, [Secret]), install));
        (await db.DistributedRateLimitBuckets.SumAsync(bucket => bucket.Count)).Should().Be(before);
        verifier.ReceivedCalls().Should().BeEmpty();
        suggestions.ReceivedCalls().Should().BeEmpty();
        Read<OnboardingInstall>(await EnrollAsync(challenge, direct));
        RotatedInstall(await SetupAsync(install, direct), install);
    }

    [Theory]
    [InlineData("::ffff:198.51.100.1", "198.51.100.1")]
    [InlineData("2001:0db8:0000::1", "2001:db8::1")]
    public async Task ForwardedAddresses_AreCanonicalBeforeCredentialAndQuotaBinding(string initial, string canonical)
    {
        var challenge = await ChallengeAsync(Forwarded(initial));
        var install = Read<OnboardingInstall>(await EnrollAsync(challenge, Forwarded(canonical))).InstallId;
        install = RotatedInstall(await SetupAsync(install, Forwarded(initial)), install);
        RotatedInstall(await SetupAsync(install, Forwarded(canonical)), install);
        db.DistributedRateLimitBuckets.Count(bucket => bucket.PolicyName == "onboarding-ip").Should().Be(1);
    }

    [Theory]
    [InlineData("android")]
    [InlineData("ios")]
    public async Task NativeCalls_KeepConnectionProofBindingReplayProtectionAndRotation(string platform)
    {
        var direct = new Client("::ffff:192.0.2.1", null, null);
        var challenge = await ChallengeAsync(direct, platform);
        AssertLimited(await EnrollAsync(challenge, direct with { ConnectionIp = "192.0.2.2" }, platform));
        var install = Read<OnboardingInstall>(await EnrollAsync(challenge, direct with { ConnectionIp = "192.0.2.1" }, platform)).InstallId;
        AssertLimited(await EnrollAsync(challenge, direct, platform));
        AssertLimited(await SetupAsync(install, direct with { ConnectionIp = "192.0.2.2" }, platform));
        var rotated = RotatedInstall(await SetupAsync(install, direct, platform), install);
        await verifier.Received(1).VerifyAsync(Proof(platform), OnboardingAccessService.BindRequest(install,
            OnboardingInput.Create("Read", "en").Value), null, 0, Arg.Any<CancellationToken>());
        AssertLimited(await SetupAsync(install, direct, platform));
        var latest = RotatedInstall(await SetupAsync(rotated, direct, platform), rotated);
        suggestions.ClearReceivedCalls();
        verifier.VerifyAsync(Arg.Any<OnboardingAttestation>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<uint>(), Arg.Any<CancellationToken>()).Returns((VerifiedOnboardingAttestation?)null);
        AssertLimited(await SetupAsync(latest, direct, platform));
        suggestions.ReceivedCalls().Should().BeEmpty();
        verifier.ClearReceivedCalls();
        AssertLimited(await SetupAsync(latest, direct, platform));
        verifier.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "VerifyAsync");
    }

    private static Client Forwarded(string visitor, string egress = Egress) => new(egress, [visitor], [Secret]);
    private static OnboardingAttestation Proof(string platform) => new(platform, "valid-proof");

    private async Task<string> ChallengeAsync(Client client, string platform = "web")
        => Read<OnboardingChallenge>(await SendAsync("challenge", new OnboardingController.ChallengeRequest(platform), client)).Challenge;

    private Task<HttpContext> EnrollAsync(string challenge, Client client, string platform = "web")
        => SendAsync("install", new OnboardingController.EnrollmentRequest(challenge, Proof(platform)), client);

    private Task<HttpContext> SetupAsync(string install, Client client, string platform = "web")
        => SendAsync("suggest-setup", new OnboardingController.SuggestSetupRequest("Read", "en", Proof(platform)), client, install);

    private async Task<HttpContext> SendAsync(string endpoint, object body, Client client, string? install = null)
    {
        using var scope = application.Services.CreateScope();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Connection.RemoteIpAddress = IPAddress.Parse(client.ConnectionIp);
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example");
        context.Request.Path = $"/api/onboarding/{endpoint}";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        context.Request.Headers["X-Forwarded-For"] = OtherVisitor;
        context.Request.Headers["Forwarded"] = $"for={OtherVisitor}";
        if (client.VisitorIps is not null)
            context.Request.Headers["X-Orbit-Client-Ip"] = client.VisitorIps;
        if (client.Secrets is not null)
            context.Request.Headers["X-Orbit-Bff-Secret"] = client.Secrets;
        if (install is not null)
            context.Request.Headers[OnboardingController.InstallHeader] = install;
        context.Response.Body = new MemoryStream();
        await pipeline(context);
        context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        return context;
    }

    private static T Read<T>(HttpContext response)
    {
        response.Response.StatusCode.Should().Be(200, Body(response));
        return JsonSerializer.Deserialize<T>(Body(response), Json) ?? throw new InvalidOperationException("Missing response body");
    }

    private static string RotatedInstall(HttpContext response, string previous)
    {
        response.Response.StatusCode.Should().Be(200, Body(response));
        var next = response.Response.Headers[OnboardingController.InstallHeader].ToString();
        next.Should().NotBeNullOrEmpty().And.NotBe(previous);
        return next;
    }

    private static void AssertLimited(HttpContext response)
    {
        response.Response.StatusCode.Should().Be(429, Body(response));
        JsonSerializer.Deserialize<OnboardingController.FallbackResponse>(Body(response), Json)
            .Should().Be(new OnboardingController.FallbackResponse("ONBOARDING_LIMIT"));
        response.Response.Headers.Should().NotContainKey(OnboardingController.InstallHeader);
    }

    private static string Body(HttpContext response) => Encoding.UTF8.GetString(((MemoryStream)response.Response.Body).ToArray());
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await application.DisposeAsync();
        await db.DisposeAsync();
    }

    private sealed record Client(string ConnectionIp, string[]? VisitorIps, string[]? Secrets);
}
