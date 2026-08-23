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

public class CreateApiKeyCommandHandlerTests
{
    private readonly IGenericRepository<ApiKey> _apiKeyRepo = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IAppConfigService _appConfigService = Substitute.For<IAppConfigService>();
    private readonly EmailChallengeService _challengeService;
    private readonly CreateApiKeyCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public CreateApiKeyCommandHandlerTests()
    {
        _challengeService = new EmailChallengeService(_cache, TimeProvider.System);
        _handler = new CreateApiKeyCommandHandler(
            _apiKeyRepo,
            _payGate,
            _unitOfWork,
            _cache,
            _appConfigService,
            _challengeService);

        _appConfigService.GetAsync(
                AppConfigKeys.RequireApiKeyCreationStepUp,
                false,
                Arg.Any<CancellationToken>())
            .Returns(true);
        AuthorizeCreation();

        _payGate.CanCreateApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        _apiKeyRepo.CountAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(0);
    }

    [Fact]
    public async Task Handle_WithoutChallenge_ReturnsNamedRefusalAndWritesNothing()
    {
        var emptyCache = new MemoryCache(new MemoryCacheOptions());
        var handler = new CreateApiKeyCommandHandler(
            _apiKeyRepo,
            _payGate,
            _unitOfWork,
            emptyCache,
            _appConfigService,
            new EmailChallengeService(emptyCache, TimeProvider.System));

        var result = await handler.Handle(
            new CreateApiKeyCommand(UserId, "My API Key"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.ApiKeyCreationChallengeRequired);
        await _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ValidCommand_CreatesKeyAndReturnsResponse()
    {
        var command = new CreateApiKeyCommand(UserId, "My API Key");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("My API Key");
        result.Value.Key.Should().StartWith("orb_");
        result.Value.KeyPrefix.Should().NotBeNullOrEmpty();
        result.Value.Id.Should().NotBeEmpty();
        await _apiKeyRepo.Received(1).AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PayGateFailure_ReturnsPayGateError()
    {
        _payGate.CanCreateApiKeys(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("API keys are a Pro feature"));

        var command = new CreateApiKeyCommand(UserId, "My Key");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        await _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MaxKeysReached_ReturnsFailure()
    {
        _apiKeyRepo.CountAsync(
            Arg.Any<Expression<Func<ApiKey, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(5);

        var command = new CreateApiKeyCommand(UserId, "One too many");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("at most 5");
        await _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyName_ReturnsFailure()
    {
        var command = new CreateApiKeyCommand(UserId, "");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("name");
        await _apiKeyRepo.DidNotReceive().AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SatisfiedChallenge_AllowsExactlyOneCreation()
    {
        var command = new CreateApiKeyCommand(UserId, "One-time grant");

        var first = await _handler.Handle(command, CancellationToken.None);
        var second = await _handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.ApiKeyCreationChallengeRequired);
        await _apiKeyRepo.Received(1).AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StepUpDisabled_CreatesWithoutChallenge()
    {
        _appConfigService.GetAsync(
                AppConfigKeys.RequireApiKeyCreationStepUp,
                false,
                Arg.Any<CancellationToken>())
            .Returns(false);
        _challengeService.TryConsumeAuthorization(EmailChallengeOperation.ApiKeyCreation, UserId);

        var result = await _handler.Handle(
            new CreateApiKeyCommand(UserId, "Kill switch path"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _apiKeyRepo.Received(1).AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    private void AuthorizeCreation() => _challengeService.AuthorizeOnce(
        EmailChallengeOperation.ApiKeyCreation,
        UserId,
        TimeSpan.FromMinutes(10));
}
