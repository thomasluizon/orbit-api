using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.ApiKeys.Services;
using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.ApiKeys;

public class GetApiKeysQueryHandlerTests : IAsyncLifetime
{
    private readonly IGenericRepository<ApiKey> _apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IAppConfigService _appConfigService = Substitute.For<IAppConfigService>();
    private readonly EmailChallengeService _challengeService;
    private readonly GetApiKeysQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetApiKeysQueryHandlerTests()
    {
        _payGate.CanReadApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _appConfigService.GetAsync(
                AppConfigKeys.RequireApiKeyCreationStepUp,
                false,
                Arg.Any<CancellationToken>())
            .Returns(true);
        _challengeService = new EmailChallengeService(ChallengeTestDatabase.For(_cache).Store(TimeProvider.System));
        _handler = new GetApiKeysQueryHandler(
            _apiKeyRepo,
            _payGate,
            _cache,
            new ApiKeyManagementAuthorization(_appConfigService, _challengeService));
    }

    public Task InitializeAsync() => _challengeService.AuthorizeOnceAsync(
        EmailChallengeOperation.ApiKeyManagement, UserId, TimeSpan.FromMinutes(10));
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Handle_WithoutChallenge_ReturnsNamedRefusal()
    {
        var emptyCache = new MemoryCache(new MemoryCacheOptions());
        var handler = new GetApiKeysQueryHandler(
            _apiKeyRepo,
            _payGate,
            emptyCache,
            new ApiKeyManagementAuthorization(_appConfigService, new EmailChallengeService(ChallengeTestDatabase.For(emptyCache).Store(TimeProvider.System))));

        var result = await handler.Handle(new GetApiKeysQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApiKeyCreationChallengeRequired);
        await _apiKeyRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ValidChallenge_AllowsRepeatedReadsWithoutConsumingGrant()
    {
        _apiKeyRepo.FindAsync(
                Arg.Any<Expression<Func<ApiKey, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey>());

        var first = await _handler.Handle(new GetApiKeysQuery(UserId), CancellationToken.None);
        var second = await _handler.Handle(new GetApiKeysQuery(UserId), CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        (await _challengeService.HasAuthorizationAsync(EmailChallengeOperation.ApiKeyManagement, UserId)).Should().BeTrue();
    }

    [Fact]
    public async Task Handle_SpentChallenge_ReturnsNamedRefusal()
    {
        await _challengeService.TryConsumeAuthorizationAsync(EmailChallengeOperation.ApiKeyManagement, UserId);

        var result = await _handler.Handle(new GetApiKeysQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApiKeyCreationChallengeRequired);
    }

    [Fact]
    public async Task Handle_ExpiredChallenge_ReturnsNamedRefusal()
    {
        var expiringCache = new MemoryCache(new MemoryCacheOptions());
        var challengeService = new EmailChallengeService(ChallengeTestDatabase.For(expiringCache).Store(TimeProvider.System));
        await challengeService.AuthorizeOnceAsync(
            EmailChallengeOperation.ApiKeyManagement,
            UserId,
            TimeSpan.FromMilliseconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        var handler = new GetApiKeysQueryHandler(
            _apiKeyRepo,
            _payGate,
            expiringCache,
            new ApiKeyManagementAuthorization(_appConfigService, challengeService));

        var result = await handler.Handle(new GetApiKeysQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApiKeyCreationChallengeRequired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_NonProWithOrWithoutChallenge_ReturnsPayGateRefusal(bool authorize)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var challengeService = new EmailChallengeService(ChallengeTestDatabase.For(cache).Store(TimeProvider.System));
        if (authorize)
        {
            await challengeService.AuthorizeOnceAsync(
                EmailChallengeOperation.ApiKeyManagement,
                UserId,
                TimeSpan.FromMinutes(10));
        }

        _payGate.CanReadApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("API keys are a Pro feature"));
        var handler = new GetApiKeysQueryHandler(
            _apiKeyRepo,
            _payGate,
            cache,
            new ApiKeyManagementAuthorization(_appConfigService, challengeService));

        var result = await handler.Handle(new GetApiKeysQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PayGate);
    }

    [Fact]
    public async Task Handle_WithKeys_ReturnsOrderedByCreatedDesc()
    {
        var (key1, _) = ApiKey.Create(UserId, "First Key").Value;
        var (key2, _) = ApiKey.Create(UserId, "Second Key").Value;

        var (foreign, _) = ApiKey.Create(Guid.NewGuid(), "Foreign").Value;
        typeof(ApiKey).GetProperty(nameof(ApiKey.CreatedAtUtc))!.SetValue(key1, DateTime.UtcNow.AddDays(-1));
        typeof(ApiKey).GetProperty(nameof(ApiKey.CreatedAtUtc))!.SetValue(key2, DateTime.UtcNow);
        _apiKeyRepo.FindAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(call => new[] { key1, foreign, key2 }.Where(call.Arg<Expression<Func<ApiKey, bool>>>().Compile()).ToList());

        var query = new GetApiKeysQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(k => k.Id).Should().Equal(key2.Id, key1.Id);
        result.Value.Should().AllSatisfy(k => k.KeyPrefix.Should().StartWith("orb_"));
    }

    [Fact]
    public async Task Handle_NoKeys_ReturnsEmptyList()
    {
        _apiKeyRepo.FindAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey>());

        var query = new GetApiKeysQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ReturnsCorrectResponseFields()
    {
        var (key, _) = ApiKey.Create(UserId, "Test Key").Value;

        _apiKeyRepo.FindAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { key });

        var query = new GetApiKeysQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var response = result.Value[0];
        response.Id.Should().Be(key.Id);
        response.Name.Should().Be("Test Key");
        response.KeyPrefix.Should().Be(key.KeyPrefix);
        response.IsRevoked.Should().BeFalse();
        response.LastUsedAtUtc.Should().BeNull();
    }
}
