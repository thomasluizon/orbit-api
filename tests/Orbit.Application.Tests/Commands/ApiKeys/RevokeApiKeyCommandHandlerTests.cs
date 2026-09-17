using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.ApiKeys;

public class RevokeApiKeyCommandHandlerTests
{
    private readonly IGenericRepository<ApiKey> _apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IAppConfigService _appConfigService = Substitute.For<IAppConfigService>();
    private readonly EmailChallengeService _challengeService;
    private readonly RevokeApiKeyCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public RevokeApiKeyCommandHandlerTests()
    {
        _payGate.CanManageApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _appConfigService.GetAsync(
                AppConfigKeys.RequireApiKeyCreationStepUp,
                false,
                Arg.Any<CancellationToken>())
            .Returns(true);
        _challengeService = new EmailChallengeService(_cache, TimeProvider.System);
        AuthorizeManagement();
        _handler = new RevokeApiKeyCommandHandler(
            _apiKeyRepo,
            _payGate,
            _unitOfWork,
            _cache,
            _appConfigService,
            _challengeService);
    }

    [Fact]
    public async Task Handle_WithoutChallenge_ReturnsNamedRefusalAndRevokesNothing()
    {
        var (apiKey, _) = ApiKey.Create(UserId, "Protected key").Value;
        _apiKeyRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<ApiKey, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { apiKey });

        var emptyCache = new MemoryCache(new MemoryCacheOptions());
        var handler = new RevokeApiKeyCommandHandler(
            _apiKeyRepo,
            _payGate,
            _unitOfWork,
            emptyCache,
            _appConfigService,
            new EmailChallengeService(emptyCache, TimeProvider.System));

        var result = await handler.Handle(
            new RevokeApiKeyCommand(UserId, apiKey.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApiKeyCreationChallengeRequired);
        apiKey.IsRevoked.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ValidCommand_RevokesKey()
    {
        var (apiKey, _) = ApiKey.Create(UserId, "My Key").Value;

        _apiKeyRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { apiKey });

        var command = new RevokeApiKeyCommand(UserId, apiKey.Id);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        apiKey.IsRevoked.Should().BeTrue();
        _challengeService.HasAuthorization(EmailChallengeOperation.ApiKeyManagement, UserId).Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SpentChallenge_ReturnsNamedRefusalAndRevokesNothing()
    {
        var (apiKey, _) = ApiKey.Create(UserId, "Protected key").Value;
        _apiKeyRepo.FindTrackedAsync(
                Arg.Any<Expression<Func<ApiKey, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey> { apiKey });
        _challengeService.TryConsumeAuthorization(EmailChallengeOperation.ApiKeyManagement, UserId);

        var result = await _handler.Handle(
            new RevokeApiKeyCommand(UserId, apiKey.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApiKeyCreationChallengeRequired);
        apiKey.IsRevoked.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ExpiredChallenge_ReturnsNamedRefusalAndRevokesNothing()
    {
        var (apiKey, _) = ApiKey.Create(UserId, "Protected key").Value;
        var expiringCache = new MemoryCache(new MemoryCacheOptions());
        var challengeService = new EmailChallengeService(expiringCache, TimeProvider.System);
        challengeService.AuthorizeOnce(
            EmailChallengeOperation.ApiKeyManagement,
            UserId,
            TimeSpan.FromMilliseconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        var handler = new RevokeApiKeyCommandHandler(
            _apiKeyRepo,
            _payGate,
            _unitOfWork,
            expiringCache,
            _appConfigService,
            challengeService);

        var result = await handler.Handle(
            new RevokeApiKeyCommand(UserId, apiKey.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApiKeyCreationChallengeRequired);
        apiKey.IsRevoked.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_NonProWithOrWithoutChallenge_ReturnsPayGateRefusal(bool authorize)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var challengeService = new EmailChallengeService(cache, TimeProvider.System);
        if (authorize)
        {
            challengeService.AuthorizeOnce(
                EmailChallengeOperation.ApiKeyManagement,
                UserId,
                TimeSpan.FromMinutes(10));
        }

        _payGate.CanManageApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("API keys are a Pro feature"));
        var handler = new RevokeApiKeyCommandHandler(
            _apiKeyRepo,
            _payGate,
            _unitOfWork,
            cache,
            _appConfigService,
            challengeService);

        var result = await handler.Handle(
            new RevokeApiKeyCommand(UserId, Guid.NewGuid()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.PayGate);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_KeyNotFound_ReturnsFailure()
    {
        _apiKeyRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<ApiKey>());

        var command = new RevokeApiKeyCommand(UserId, Guid.NewGuid());
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.ApiKeyNotFound.Message);
        result.ErrorCode.Should().Be(ErrorCodes.ApiKeyNotFound);
    }

    private void AuthorizeManagement() => _challengeService.AuthorizeOnce(
        EmailChallengeOperation.ApiKeyManagement,
        UserId,
        TimeSpan.FromMinutes(10));
}
