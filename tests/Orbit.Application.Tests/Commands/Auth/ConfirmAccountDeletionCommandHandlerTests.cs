using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using NSubstitute.ExceptionExtensions;
using Orbit.Domain.Enums;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Auth.Services;
using Orbit.Application.Behaviors;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class ConfirmAccountDeletionCommandHandlerTests
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly ConfirmAccountDeletionCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private const string TestEmail = "test@example.com";

    public ConfirmAccountDeletionCommandHandlerTests()
    {
        _handler = new ConfirmAccountDeletionCommandHandler(
            new EmailChallengeService(_cache, TimeProvider.System),
            _userRepo,
            _unitOfWork, _jobs);
    }

    [Fact]
    public async Task Handle_ValidCode_DeactivatesUserAndReturnsScheduledDate()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");

        var command = new ConfirmAccountDeletionCommand(UserId, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeAfter(DateTime.UtcNow);
        user.IsDeactivated.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _cache.TryGetValue($"delete:{TestEmail}", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ProPlanBeyondGraceCap_SchedulesDeletionAtThirtyDays()
    {
        var user = User.Create("Test", TestEmail).Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(340));
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");
        var earliestScheduledDeletionAtUtc = DateTime.UtcNow.AddDays(30);

        var result = await _handler.Handle(
            new ConfirmAccountDeletionCommand(UserId, "123456"),
            CancellationToken.None);

        var latestScheduledDeletionAtUtc = DateTime.UtcNow.AddDays(30);
        result.Value.Should().BeOnOrAfter(earliestScheduledDeletionAtUtc)
            .And.BeOnOrBefore(latestScheduledDeletionAtUtc);
    }

    [Fact]
    public async Task Handle_ProPlanWithinGraceCap_SchedulesDeletionSevenDaysAfterPlanExpiry()
    {
        var user = User.Create("Test", TestEmail).Value;
        var planExpiresAtUtc = DateTime.UtcNow.AddDays(3);
        user.SetStripeSubscription("sub_123", planExpiresAtUtc);
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");

        var result = await _handler.Handle(
            new ConfirmAccountDeletionCommand(UserId, "123456"),
            CancellationToken.None);

        result.Value.Should().Be(planExpiresAtUtc.AddDays(7));
        _jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [Fact]
    public async Task Handle_ProPlanAtGraceCapBoundary_SchedulesDeletionSevenDaysAfterPlanExpiry()
    {
        var user = User.Create("Test", TestEmail).Value;
        var planExpiresAtUtc = DateTime.UtcNow.AddDays(23);
        user.SetStripeSubscription("sub_123", planExpiresAtUtc);
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");

        var result = await _handler.Handle(
            new ConfirmAccountDeletionCommand(UserId, "123456"),
            CancellationToken.None);

        result.Value.Should().Be(planExpiresAtUtc.AddDays(7));
    }

    [Fact]
    public async Task Handle_FreeUser_SchedulesDeletionInSevenDays()
    {
        var user = User.Create("Test", TestEmail).Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");
        var earliestScheduledDeletionAtUtc = DateTime.UtcNow.AddDays(7);

        var result = await _handler.Handle(
            new ConfirmAccountDeletionCommand(UserId, "123456"),
            CancellationToken.None);

        _jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        var latestScheduledDeletionAtUtc = DateTime.UtcNow.AddDays(7);
        result.Value.Should().BeOnOrAfter(earliestScheduledDeletionAtUtc)
            .And.BeOnOrBefore(latestScheduledDeletionAtUtc);
    }

    [Fact]
    public async Task Handle_TrialUserWithoutPlanExpiry_SchedulesDeletionInSevenDays()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");
        var earliestScheduledDeletionAtUtc = DateTime.UtcNow.AddDays(7);

        var result = await _handler.Handle(
            new ConfirmAccountDeletionCommand(UserId, "123456"),
            CancellationToken.None);

        _jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        var latestScheduledDeletionAtUtc = DateTime.UtcNow.AddDays(7);
        result.Value.Should().BeOnOrAfter(earliestScheduledDeletionAtUtc)
            .And.BeOnOrBefore(latestScheduledDeletionAtUtc);
    }

    [Fact]
    public async Task Handle_ExpiredProPlan_SchedulesDeletionInSevenDays()
    {
        var user = User.Create("Test", TestEmail).Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(-1));
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");
        var earliestScheduledDeletionAtUtc = DateTime.UtcNow.AddDays(7);

        var result = await _handler.Handle(
            new ConfirmAccountDeletionCommand(UserId, "123456"),
            CancellationToken.None);

        var latestScheduledDeletionAtUtc = DateTime.UtcNow.AddDays(7);
        result.Value.Should().BeOnOrAfter(earliestScheduledDeletionAtUtc)
            .And.BeOnOrBefore(latestScheduledDeletionAtUtc);
    }

    [Fact]
    public async Task Handle_ValidCode_ClearsGoogleOAuthTokens()
    {
        var user = User.Create("Test", TestEmail).Value;
        user.SetGoogleTokens("access-token", "refresh-token");
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");

        var result = await _handler.Handle(new ConfirmAccountDeletionCommand(UserId, "123456"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.GoogleAccessToken.Should().BeNull();
        user.GoogleRefreshToken.Should().BeNull();
    }

    [Fact]
    public async Task Handle_InvalidCode_LeavesGoogleOAuthTokensIntact()
    {
        var user = User.Create("Test", TestEmail).Value;
        user.SetGoogleTokens("access-token", "refresh-token");
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");

        var result = await _handler.Handle(new ConfirmAccountDeletionCommand(UserId, "999999"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        user.GoogleAccessToken.Should().Be("access-token");
        user.GoogleRefreshToken.Should().Be("refresh-token");
    }

    [Fact]
    public async Task Handle_InvalidCode_ReturnsFailureAndIncrementsAttempts()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");

        var command = new ConfirmAccountDeletionCommand(UserId, "999999");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.InvalidDeletionCode.Format(2).Message);
        user.IsDeactivated.Should().BeFalse();
        user.ScheduledDeletionAt.Should().BeNull();
        _jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        _cache.TryGetValue($"delete-attempts:{TestEmail}", out int attempts).Should().BeTrue();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Handle_AttemptBudgetSurvivesResend_LocksAfterMaxAcrossNewCodes()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            SetupDeletionCode(TestEmail, "123456");
            var wrong = await _handler.Handle(new ConfirmAccountDeletionCommand(UserId, "999999"), CancellationToken.None);
            wrong.Error.Should().Be(ErrorMessages.InvalidDeletionCode.Format(AppConstants.MaxVerificationAttempts - attempt - 1).Message);
        }

        SetupDeletionCode(TestEmail, "123456");
        var result = await _handler.Handle(new ConfirmAccountDeletionCommand(UserId, "123456"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.TooManyCodeAttempts.Message);
        user.IsDeactivated.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        var command = new ConfirmAccountDeletionCommand(UserId, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.UserNotFound.Message);
    }

    [Fact]
    public async Task Handle_ExpiredCode_ReturnsFailure()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);

        var command = new ConfirmAccountDeletionCommand(UserId, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("expired");
    }

    [Fact]
    public async Task Handle_MaxAttemptsReached_ReturnsFailureWithoutDeactivating()
    {
        var user = User.Create("Test", TestEmail).Value;
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");

        _cache.Set($"delete-attempts:{TestEmail}", 3,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) });

        var command = new ConfirmAccountDeletionCommand(UserId, "123456");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.TooManyCodeAttempts.Message);
        user.IsDeactivated.Should().BeFalse();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Command_IsNotConcurrencyRetryable_BecauseItConsumesTheOtpBeforeSaving()
    {
        typeof(ConfirmAccountDeletionCommand).Should().NotBeAssignableTo<IConcurrencyRetryable>(
            "the handler removes the OTP from cache before SaveChangesAsync, so a transparent retry would re-enter, "
            + "find the code consumed, and silently fail with DeletionCodeExpired");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_PaidSubscription_EnqueuesOnlySelectedProviderAfterSaving(bool play)
    {
        var user = User.Create("Paid", TestEmail).Value;
        var expiresAtUtc = DateTime.UtcNow.AddDays(20);
        if (play)
            user.SetPlaySubscription("play-token", expiresAtUtc);
        else
            user.SetStripeSubscription("sub_paid", expiresAtUtc);
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");
        var saved = false;
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            saved = true;
            return 1;
        });
        Job? enqueued = null;
        _jobs.Create(Arg.Do<Job>(job =>
        {
            saved.Should().BeTrue();
            user.IsDeactivated.Should().BeTrue();
            enqueued = job;
        }), Arg.Any<IState>());

        var result = await _handler.Handle(new ConfirmAccountDeletionCommand(UserId, "123456"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expiresAtUtc.AddDays(7));
        user.PlanExpiresAt.Should().Be(expiresAtUtc);
        _jobs.Received(1).Create(Arg.Any<Job>(), Arg.Is<IState>(state => state is EnqueuedState));
        enqueued!.Type.Should().Be<CancelSubscriptionOnDeletionJob>();
        enqueued.Method.Name.Should().Be(nameof(CancelSubscriptionOnDeletionJob.ExecuteAsync));
        enqueued.Args.Should().Equal(user.Id, play ? null : "sub_paid", play ? "play-token" : null, true, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_ProviderOutage_ConfirmationSucceedsAndQueuedJobFailsForRetry()
    {
        var user = User.Create("Paid", TestEmail).Value;
        user.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(20));
        SetupUser(user);
        SetupDeletionCode(TestEmail, "123456");
        Job? enqueued = null;
        _jobs.Create(Arg.Do<Job>(job => enqueued = job), Arg.Any<IState>());
        var stripe = Substitute.For<IBillingService>();
        var play = Substitute.For<IPlayBillingService>();
        stripe.GetSubscriptionDetailsAsync("sub_paid", Arg.Any<CancellationToken>())
            .Returns(new BillingSubscriptionDetails("active", user.PlanExpiresAt!.Value, false, SubscriptionInterval.Monthly, 1000, "usd", null));
        stripe.SetSubscriptionCancelAtPeriodEndAsync("sub_paid", true, Arg.Any<CancellationToken>())
            .ThrowsAsync(new BillingProviderException("unavailable"));
        _userRepo.FindOneTrackedIgnoringFiltersAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>()).Returns(user);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));

        var result = await _handler.Handle(new ConfirmAccountDeletionCommand(UserId, "123456"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.IsDeactivated.Should().BeTrue();
        enqueued.Should().NotBeNull();
        stripe.ReceivedCalls().Should().BeEmpty();
        var job = new CancelSubscriptionOnDeletionJob(_userRepo, _unitOfWork, stripe, play, Options.Create(new GooglePlaySettings()));
        var execute = () => job.ExecuteAsync((Guid)enqueued!.Args[0], (string?)enqueued.Args[1], (string?)enqueued.Args[2],
            (bool)enqueued.Args[3], CancellationToken.None);
        await execute.Should().ThrowAsync<BillingProviderException>();
    }

    private void SetupUser(User user)
    {
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);
    }

    private void SetupDeletionCode(string email, string code)
    {
        var entry = new VerificationEntry(code, 0, DateTime.UtcNow);
        _cache.Set($"delete:{email.ToLowerInvariant()}", entry,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
    }
}
