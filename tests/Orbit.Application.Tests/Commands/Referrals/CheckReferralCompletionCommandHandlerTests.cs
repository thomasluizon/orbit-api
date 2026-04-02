using FluentAssertions;
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
        _handler = new CheckReferralCompletionCommandHandler(
            _userRepo, _referralRepo, _habitRepo, _habitLogRepo,
            _notificationRepo, _pushNotification,
            _referralReward, _unitOfWork,
            Substitute.For<ILogger<CheckReferralCompletionCommandHandler>>());

        _referralReward.CreateReferralCouponAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
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
        // First call returns referred user, second call returns referrer
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
                var habit = Habit.Create(userId, "Test Habit", FrequencyUnit.Day, 1).Value;
                return habit;
            })
            .ToList();

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(habits);

        // Create log instances via private constructor (only Count matters for threshold check)
        var logs = new List<HabitLog>();
        for (var i = 0; i < logCount; i++)
        {
            var log = (HabitLog)Activator.CreateInstance(typeof(HabitLog), nonPublic: true)!;
            typeof(HabitLog).GetProperty("HabitId")!.SetValue(log, habits.First().Id);
            logs.Add(log);
        }

        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(logs);
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

        // Only 1 habit with 2 logs (below threshold of 3)
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
        // Coupon should be created for the referrer
        await _referralReward.Received(1).CreateReferralCouponAsync(
            ReferrerId, Arg.Any<CancellationToken>());
        referrer.ReferralCouponId.Should().Be("promo_test123");
        await _unitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ThresholdMet_ProReferrer_StillGetsCoupon()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        var referrer = CreateReferrer();
        // Make referrer a Pro user with an active Stripe subscription
        referrer.SetStripeSubscription("sub_test123", DateTime.UtcNow.AddDays(30));
        SetupPendingReferral(referral);
        SetupReferredAndReferrerUsers(referredUser, referrer);
        SetupHabitsAndLogs(ReferredUserId, 1, AppConstants.ReferralCompletionThreshold);

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        referral.Status.Should().Be(ReferralStatus.Rewarded);
        // Pro users also get a coupon (same behavior for all users now)
        await _referralReward.Received(1).CreateReferralCouponAsync(
            ReferrerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CompletionWindowExpired_ReturnsSuccessNoOp()
    {
        var referral = CreatePendingReferral();
        var referredUser = CreateReferredUser();
        // Force CreatedAtUtc to be beyond the completion window
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

        // No habits
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>());

        var command = new CheckReferralCompletionCommand(ReferredUserId);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        referral.Status.Should().Be(ReferralStatus.Pending);
        // HabitLog repo should not even be queried when no habits exist
        await _habitLogRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>());
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
