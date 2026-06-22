using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Referrals;

public class CheckReferralCompletionCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Referral> _referralRepo = Substitute.For<IGenericRepository<Referral>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Notification> _notificationRepo = Substitute.For<IGenericRepository<Notification>>();
    private readonly IPushNotificationService _pushNotification = Substitute.For<IPushNotificationService>();
    private readonly IReferralRewardService _referralReward = Substitute.For<IReferralRewardService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CheckReferralCompletionCommandHandler _handler;

    private static readonly Guid ReferrerId = Guid.NewGuid();
    private static readonly Guid ReferredUserId = Guid.NewGuid();

    public CheckReferralCompletionCommandHandlerTests()
    {
        var repos = new ReferralRepositories(
            _userRepo, _referralRepo, _habitRepo, _habitLogRepo, _notificationRepo);
        _handler = new CheckReferralCompletionCommandHandler(
            repos, _pushNotification, _referralReward, _unitOfWork,
            Substitute.For<ILogger<CheckReferralCompletionCommandHandler>>());

        _referralReward.CreateReferralCouponAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("promo_test123");
    }

    private static User CreateReferrer()
    {
        var user = User.Create("Referrer", "referrer@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, ReferrerId);
        return user;
    }

    private static User CreateReferredUser()
    {
        var user = User.Create("Referred", "referred@example.com").Value;
        typeof(User).GetProperty("Id")!.SetValue(user, ReferredUserId);
        return user;
    }

    private static Referral CreatePendingReferral()
    {
        return Referral.Create(ReferrerId, ReferredUserId);
    }

    private void SetupNoPendingReferrals()
    {
        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral>());
    }

    private void SetupPendingReferral(Referral referral)
    {
        _referralRepo.FindAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Referral> { referral });

        _referralRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Referral, bool>>>(),
            Arg.Any<Func<IQueryable<Referral>, IQueryable<Referral>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(referral);
    }

    private void SetupReferredUserFound(User user)
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }

    private void SetupReferredAndReferrerUsers(User referredUser, User referrer)
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(referredUser, referrer);
    }

    private void SetupHabitsAndLogs(Guid userId, int habitCount, int logCount)
    {
        var habits = Enumerable.Range(0, habitCount)
            .Select(_ =>
            {
                var habit = Habit.Create(new HabitCreateParams(userId, "Test Habit", FrequencyUnit.Day, 1)).Value;
                return habit;
            })
            .ToList();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(habits);

        _habitLogRepo.CountAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(logCount);
    }

    [Fact]
    public async Task Handle_NoPendingReferral_ReturnsSuccessNoOp()
    {
        SetupNoPendingReferrals();

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InsufficientLogs_ReturnsSuccessNoOp()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        SetupPendingReferral(referral);
        SetupReferredUserFound(referredUser);

        SetupHabitsAndLogs(ReferredUserId, 1, AppConstants.ReferralCompletionThreshold - 1);

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        referral.Status.Should().Be(ReferralStatus.Pending);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ThresholdMet_CreatesCouponForReferrer()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        var referrer = CreateReferrer();
        SetupPendingReferral(referral);
        SetupReferredAndReferrerUsers(referredUser, referrer);
        SetupHabitsAndLogs(ReferredUserId, 1, AppConstants.ReferralCompletionThreshold);

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        referral.Status.Should().Be(ReferralStatus.Rewarded);
        referral.CompletedAtUtc.Should().NotBeNull();
        referral.RewardGrantedAtUtc.Should().NotBeNull();
        await _referralReward.Received(1).CreateReferralCouponAsync(
            ReferrerId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        referrer.ReferralCouponId.Should().Be("promo_test123");
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ThresholdMet_ProReferrer_StillGetsCoupon()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        var referrer = CreateReferrer();
        referrer.SetStripeSubscription("sub_test123", DateTime.UtcNow.AddDays(30));
        SetupPendingReferral(referral);
        SetupReferredAndReferrerUsers(referredUser, referrer);
        SetupHabitsAndLogs(ReferredUserId, 1, AppConstants.ReferralCompletionThreshold);

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        referral.Status.Should().Be(ReferralStatus.Rewarded);
        await _referralReward.Received(1).CreateReferralCouponAsync(
            ReferrerId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CompletionWindowExpired_ReturnsSuccessNoOp()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        typeof(User).GetProperty("CreatedAtUtc")!.SetValue(
            referredUser,
            DateTime.UtcNow.AddDays(-(AppConstants.ReferralCompletionWindowDays + 1)));
        SetupPendingReferral(referral);
        SetupReferredUserFound(referredUser);

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        referral.Status.Should().Be(ReferralStatus.Pending);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoHabits_ReturnsSuccessNoOp()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        SetupPendingReferral(referral);
        SetupReferredUserFound(referredUser);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>());

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        referral.Status.Should().Be(ReferralStatus.Pending);
        await _habitLogRepo.DidNotReceive().CountAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RunTwice_DoesNotDoubleGrantCoupon()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        var referrer = CreateReferrer();
        SetupPendingReferral(referral);
        _userRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(referredUser, referrer, referredUser, referrer);
        SetupHabitsAndLogs(ReferredUserId, 1, AppConstants.ReferralCompletionThreshold);

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        var first = await _handler.Handle(command, CancellationToken.None);
        var second = await _handler.Handle(command, CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        referral.Status.Should().Be(ReferralStatus.Rewarded);
        await _referralReward.Received(1).CreateReferralCouponAsync(
            ReferrerId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _referralReward.Received(1).CreateReferralCouponAsync(
            ReferredUserId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ConcurrentClaimConflict_ReturnsSuccessWithoutGrantingCoupon()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        var referrer = CreateReferrer();
        SetupPendingReferral(referral);
        SetupReferredAndReferrerUsers(referredUser, referrer);
        SetupHabitsAndLogs(ReferredUserId, 1, AppConstants.ReferralCompletionThreshold);

        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new DbUpdateConcurrencyException("simulated concurrent claim")));

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _referralReward.DidNotReceive().CreateReferralCouponAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RewardFlagPersistedBeforeCouponGranted()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        var referrer = CreateReferrer();
        SetupPendingReferral(referral);
        SetupReferredAndReferrerUsers(referredUser, referrer);
        SetupHabitsAndLogs(ReferredUserId, 1, AppConstants.ReferralCompletionThreshold);

        var rewardedWhenCouponCreated = false;
        _referralReward
            .CreateReferralCouponAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                rewardedWhenCouponCreated = referral.Status == ReferralStatus.Rewarded;
                return "promo_test123";
            });

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        await _handler.Handle(command, CancellationToken.None);

        rewardedWhenCouponCreated.Should().BeTrue();
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ThresholdMet_SendsNotificationToReferrer()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        var referrer = CreateReferrer();
        SetupPendingReferral(referral);
        SetupReferredAndReferrerUsers(referredUser, referrer);
        SetupHabitsAndLogs(ReferredUserId, 1, AppConstants.ReferralCompletionThreshold);

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        await _handler.Handle(command, CancellationToken.None);

        await _notificationRepo.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.UserId == ReferrerId),
            Arg.Any<CancellationToken>());
    }
}
