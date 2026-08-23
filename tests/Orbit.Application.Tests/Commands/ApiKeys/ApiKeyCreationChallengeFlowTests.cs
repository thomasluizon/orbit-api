using System.Linq.Expressions;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Jobs;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.ApiKeys;

public class ApiKeyCreationChallengeFlowTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string Email = "step-up@example.com";

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly MutableTimeProvider _timeProvider = new(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<ApiKey> _apiKeyRepository = Substitute.For<IGenericRepository<ApiKey>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAppConfigService _appConfigService = Substitute.For<IAppConfigService>();
    private readonly IBackgroundJobClient _backgroundJobClient = Substitute.For<IBackgroundJobClient>();
    private readonly EmailChallengeService _challengeService;
    private readonly RequestApiKeyCreationChallengeCommandHandler _requestHandler;
    private readonly ConfirmApiKeyCreationChallengeCommandHandler _confirmHandler;
    private readonly CreateApiKeyCommandHandler _createHandler;
    private Job? _enqueuedJob;

    public ApiKeyCreationChallengeFlowTests()
    {
        var user = User.Create("Step Up User", Email).Value;
        _userRepository.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _payGate.CanCreateApiKeys(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _apiKeyRepository.CountAsync(
                Arg.Any<Expression<Func<ApiKey, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(0);
        _appConfigService.GetAsync(
                AppConfigKeys.RequireApiKeyCreationStepUp,
                true,
                Arg.Any<CancellationToken>())
            .Returns(true);
        _backgroundJobClient.Create(Arg.Do<Job>(job => _enqueuedJob = job), Arg.Any<IState>());

        _challengeService = new EmailChallengeService(_cache, _timeProvider);
        _requestHandler = new RequestApiKeyCreationChallengeCommandHandler(
            _challengeService,
            _userRepository,
            _backgroundJobClient);
        _confirmHandler = new ConfirmApiKeyCreationChallengeCommandHandler(
            _challengeService,
            _userRepository);
        _createHandler = new CreateApiKeyCommandHandler(
            _apiKeyRepository,
            _payGate,
            _unitOfWork,
            _cache,
            _appConfigService,
            _challengeService);
    }

    [Fact]
    public async Task RequestConfirmAndCreate_ValidCode_CreatesOneKey()
    {
        var requestResult = await RequestChallenge();
        var code = CachedCode();

        var confirmationResult = await _confirmHandler.Handle(
            new ConfirmApiKeyCreationChallengeCommand(UserId, code),
            CancellationToken.None);
        var createResult = await _createHandler.Handle(
            new CreateApiKeyCommand(UserId, "Confirmed key"),
            CancellationToken.None);

        requestResult.IsSuccess.Should().BeTrue();
        _enqueuedJob.Should().NotBeNull();
        _enqueuedJob!.Type.Should().Be<SendApiKeyCreationCodeEmailJob>();
        _enqueuedJob.Args.Should().Equal(Email, code, "en");
        confirmationResult.IsSuccess.Should().BeTrue();
        createResult.IsSuccess.Should().BeTrue();
        await _apiKeyRepository.Received(1).AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirm_WrongCodeThreeTimes_FourthAttemptIsExhausted()
    {
        await RequestChallenge();

        for (var attempt = 1; attempt <= AppConstants.MaxVerificationAttempts; attempt++)
        {
            var result = await _confirmHandler.Handle(
                new ConfirmApiKeyCreationChallengeCommand(UserId, "999999"),
                CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.Error.Should().Contain($"Remaining attempts: {AppConstants.MaxVerificationAttempts - attempt}");
        }

        var exhausted = await _confirmHandler.Handle(
            new ConfirmApiKeyCreationChallengeCommand(UserId, CachedCode()),
            CancellationToken.None);

        exhausted.IsFailure.Should().BeTrue();
        exhausted.ErrorCode.Should().Be(ErrorCodes.TooManyAttempts);
    }

    [Fact]
    public async Task Confirm_AfterTenMinutes_ReturnsExpired()
    {
        await RequestChallenge();
        var code = CachedCode();
        _timeProvider.Advance(TimeSpan.FromMinutes(AppConstants.SensitiveOperationChallengeTtlMinutes));

        var result = await _confirmHandler.Handle(
            new ConfirmApiKeyCreationChallengeCommand(UserId, code),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CodeExpired);
        _challengeService.HasAuthorization(EmailChallengeOperation.ApiKeyCreation, UserId).Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_SameCodeConcurrently_AuthorizesOnlyOneCreation()
    {
        await RequestChallenge();
        var code = CachedCode();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Result> ConfirmAfterStartAsync()
        {
            await start.Task;
            return await _confirmHandler.Handle(
                new ConfirmApiKeyCreationChallengeCommand(UserId, code),
                CancellationToken.None);
        }

        var confirmationTasks = new[]
        {
            Task.Run(ConfirmAfterStartAsync),
            Task.Run(ConfirmAfterStartAsync),
        };
        start.SetResult();
        var confirmations = await Task.WhenAll(confirmationTasks);

        var firstCreate = await _createHandler.Handle(
            new CreateApiKeyCommand(UserId, "Concurrent grant one"),
            CancellationToken.None);
        var secondCreate = await _createHandler.Handle(
            new CreateApiKeyCommand(UserId, "Concurrent grant two"),
            CancellationToken.None);

        confirmations.Count(result => result.IsSuccess).Should().Be(1);
        confirmations.Count(result => result.IsFailure).Should().Be(1);
        new[] { firstCreate, secondCreate }.Count(result => result.IsSuccess).Should().Be(1);
        await _apiKeyRepository.Received(1).AddAsync(Arg.Any<ApiKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Request_TwiceWithinSixtySeconds_ReturnsCooldownAndSendsOneEmail()
    {
        var first = await RequestChallenge();
        var second = await RequestChallenge();

        first.IsSuccess.Should().BeTrue();
        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(ErrorCodes.CodeRequestCooldown);
        _backgroundJobClient.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    private Task<Result> RequestChallenge() => _requestHandler.Handle(
        new RequestApiKeyCreationChallengeCommand(UserId),
        CancellationToken.None);

    private string CachedCode()
    {
        _cache.TryGetValue($"api-key-create:{Email}", out VerificationEntry? entry).Should().BeTrue();
        return entry!.Code;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
