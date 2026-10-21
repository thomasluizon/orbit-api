using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Authentication;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Authentication;

public class ApiKeyAuthenticationHandlerTests
{
    private sealed record HandlerRun(
        AuthenticateResult Result,
        IGenericRepository<ApiKey> ApiKeyRepo,
        IUnitOfWork UnitOfWork);

    private static async Task<HandlerRun> RunHandler(
        string? authorizationHeader,
        string path = "/mcp",
        IReadOnlyList<ApiKey>? candidates = null,
        Result? payGateResult = null)
    {
        var apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
        var payGate = Substitute.For<IPayGateService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        payGate.CanReadApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(payGateResult ?? Result.Success()));
        apiKeyRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<ApiKey, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(candidates ?? []);

        var services = new ServiceCollection();
        services.AddSingleton(apiKeyRepo);
        services.AddSingleton(payGate);
        services.AddSingleton(unitOfWork);
        services.AddSingleton(Substitute.For<IApiKeyUsageTracker>());
        services.AddMemoryCache();
        var serviceProvider = services.BuildServiceProvider();

        var optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new ApiKeyAuthenticationHandler(
            optionsMonitor, new NullLoggerFactory(), UrlEncoder.Default, serviceProvider);

        var scheme = new AuthenticationScheme("ApiKey", "ApiKey", typeof(ApiKeyAuthenticationHandler));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        if (authorizationHeader is not null)
            httpContext.Request.Headers.Authorization = authorizationHeader;

        await handler.InitializeAsync(scheme, httpContext);
        var result = await handler.AuthenticateAsync();
        return new HandlerRun(result, apiKeyRepo, unitOfWork);
    }

    private static Expression<Func<ApiKey, bool>> CapturedPredicate(IGenericRepository<ApiKey> repo) =>
        (Expression<Func<ApiKey, bool>>)repo.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == "FindTrackedAsync")
            .GetArguments()[0]!;

    [Fact]
    public async Task LegacyKey_RepeatedAuthentication_PreservesRollbackReadableHash()
    {
        var (key, raw) = ApiKey.Create(Guid.NewGuid(), "Legacy").Value;
        var legacyHash = BCrypt.Net.BCrypt.HashPassword(raw);
        legacyHash.Should().StartWith("$2");
        typeof(ApiKey).GetProperty(nameof(ApiKey.KeyHash))!.SetValue(key, legacyHash);
        for (var i = 0; i < 3; i++)
        {
            var run = await RunHandler($"Bearer {raw}", candidates: [key]);

            run.Result.Succeeded.Should().BeTrue();
            BCrypt.Net.BCrypt.Verify(raw, key.KeyHash).Should().BeTrue();
            key.KeyHash.Should().Be(legacyHash);
            await run.UnitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
            await run.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
            await run.ApiKeyRepo.DidNotReceive().ReloadAsync(key, Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task LegacyKey_WrongKey_RejectsAuthenticationWithoutWritingHash()
    {
        var (key, raw) = ApiKey.Create(Guid.NewGuid(), "Legacy").Value;
        var legacyHash = BCrypt.Net.BCrypt.HashPassword(raw);
        typeof(ApiKey).GetProperty(nameof(ApiKey.KeyHash))!.SetValue(key, legacyHash);

        var run = await RunHandler($"Bearer {raw[..^1]}!", candidates: [key]);

        run.Result.Succeeded.Should().BeFalse();
        key.KeyHash.Should().Be(legacyHash);
        await run.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RapidCalls_DoNotWriteRecentlyUsedKey()
    {
        var (key, raw) = ApiKey.Create(Guid.NewGuid(), "Rapid").Value;
        key.MarkUsed();
        for (var i = 0; i < 5; i++)
        {
            var run = await RunHandler($"Bearer {raw}", candidates: [key]);
            run.Result.Succeeded.Should().BeTrue();
            await run.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task NewKey_RemainsReadableByRollbackVerifier_AndTamperingFails()
    {
        var (key, raw) = ApiKey.Create(Guid.NewGuid(), "Hash").Value;
        BCrypt.Net.BCrypt.Verify(raw, key.KeyHash).Should().BeTrue();
        (await RunHandler($"Bearer {raw}", candidates: [key])).Result.Succeeded.Should().BeTrue();
        (await RunHandler($"Bearer {raw[..^1]}!", candidates: [key])).Result.Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagingSha256Key_AuthenticatesWithoutWritingHash(bool lowercase)
    {
        var (key, raw) = ApiKey.Create(Guid.NewGuid(), "Staging").Value;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
        if (lowercase)
            hash = hash.ToLowerInvariant();
        typeof(ApiKey).GetProperty(nameof(ApiKey.KeyHash))!.SetValue(key, hash);

        for (var i = 0; i < 3; i++)
        {
            var run = await RunHandler($"Bearer {raw}", candidates: [key]);

            run.Result.Succeeded.Should().BeTrue();
            key.KeyHash.Should().Be(hash);
            await run.UnitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        }

        (await RunHandler($"Bearer {raw[..^1]}!", candidates: [key])).Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_MissingHeader_ReturnsFail()
    {
        var run = await RunHandler(null);

        run.Result.Succeeded.Should().BeFalse();
        run.Result.Failure!.Message.Should().Contain("Not an API key");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_EmptyHeader_ReturnsFail()
    {
        var run = await RunHandler("");

        run.Result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_NonApiKeyBearer_ReturnsFail()
    {
        var run = await RunHandler("Bearer eyJhbGciOiJIUzI1NiJ9.test");

        run.Result.Succeeded.Should().BeFalse();
        run.Result.Failure!.Message.Should().Contain("Not an API key");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ApiKeyOnNonAgentPath_ReturnsFail()
    {
        var run = await RunHandler($"Bearer orb_{new string('a', 20)}", path: "/api/habits");

        run.Result.Succeeded.Should().BeFalse();
        run.Result.Failure!.Message.Should().Contain("agent endpoints");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_TooShortApiKey_ReturnsFail()
    {
        var run = await RunHandler("Bearer orb_short");

        run.Result.Succeeded.Should().BeFalse();
        run.Result.Failure!.Message.Should().Contain("Invalid API key format");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ValidFormatButNoMatch_ReturnsFail()
    {
        var run = await RunHandler("Bearer orb_testkey12345678", candidates: []);

        run.Result.Succeeded.Should().BeFalse();
        run.Result.Failure!.Message.Should().Contain("Invalid API key");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ValidKey_SucceedsWithIdentityClaims()
    {
        var userId = Guid.NewGuid();
        var (apiKey, rawKey) = ApiKey.Create(
            userId, "Agent Key", ["habits:read", "goals:read"], isReadOnly: true).Value;

        var run = await RunHandler($"Bearer {rawKey}", candidates: [apiKey]);

        run.Result.Succeeded.Should().BeTrue();
        var principal = run.Result.Principal!;
        principal.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(userId.ToString());
        principal.FindFirst("auth_method")!.Value.Should().Be("api_key");
        principal.FindFirst("api_key_id")!.Value.Should().Be(apiKey.Id.ToString());
        principal.FindFirst("api_key_read_only")!.Value.Should().Be("True");
        principal.FindAll("scope").Select(claim => claim.Value)
            .Should().BeEquivalentTo("habits:read", "goals:read");
        await run.UnitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_StoredSocialScope_DoesNotRejectOtherGrantedScopes()
    {
        var (apiKey, rawKey) = ApiKey.Create(Guid.NewGuid(), "Existing key",
            ["manage_social", AgentScopes.ReadHabits]).Value;

        var run = await RunHandler($"Bearer {rawKey}", candidates: [apiKey]);

        run.Result.Succeeded.Should().BeTrue();
        run.Result.Principal!.FindAll("scope").Select(claim => claim.Value)
            .Should().BeEquivalentTo("manage_social", AgentScopes.ReadHabits);
        AgentScopes.All.Should().NotContain("manage_social");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_ExpiredKey_ReturnsFail()
    {
        var (apiKey, rawKey) = ApiKey.Create(Guid.NewGuid(), "Agent Key").Value;
        typeof(ApiKey).GetProperty(nameof(ApiKey.ExpiresAtUtc))!
            .SetValue(apiKey, DateTime.UtcNow.AddDays(-1));

        var run = await RunHandler($"Bearer {rawKey}", candidates: [apiKey]);

        run.Result.Succeeded.Should().BeFalse();
        run.Result.Failure!.Message.Should().Contain("expired");
        await run.UnitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task HandleAuthenticateAsync_PayGateDenied_ReturnsFail()
    {
        var (apiKey, rawKey) = ApiKey.Create(Guid.NewGuid(), "Agent Key").Value;

        var run = await RunHandler(
            $"Bearer {rawKey}", candidates: [apiKey], payGateResult: Result.Failure("no plan"));

        run.Result.Succeeded.Should().BeFalse();
        run.Result.Failure!.Message.Should().Contain("not available for this plan");
    }

    [Fact]
    public async Task HandleAuthenticateAsync_QueryPredicateExcludesRevokedKeys()
    {
        var (apiKey, rawKey) = ApiKey.Create(Guid.NewGuid(), "Agent Key").Value;

        var run = await RunHandler($"Bearer {rawKey}", candidates: []);

        var predicate = CapturedPredicate(run.ApiKeyRepo).Compile();
        predicate(apiKey).Should().BeTrue();

        apiKey.Revoke();
        predicate(apiKey).Should().BeFalse();
    }
}
