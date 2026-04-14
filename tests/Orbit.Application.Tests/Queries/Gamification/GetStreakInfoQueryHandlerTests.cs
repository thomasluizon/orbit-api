using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetStreakInfoQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetStreakInfoQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 15);

    public GetStreakInfoQueryHandlerTests()
    {
        _handler = new GetStreakInfoQueryHandler(_userRepo, _streakFreezeRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsStreakInfo()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentStreak.Should().Be(0);
        result.Value.LongestStreak.Should().Be(0);
        result.Value.FreezesUsedThisMonth.Should().Be(0);
        // With zero balance, nothing is available (merit-based model).
        result.Value.FreezesAvailable.Should().Be(0);
        result.Value.MaxFreezesPerMonth.Should().Be(3);
        result.Value.MaxFreezesHeld.Should().Be(3);
        result.Value.StreakFreezeBalance.Should().Be(0);
        result.Value.ProgressToNextFreeze.Should().Be(0);
        result.Value.DaysUntilNextFreeze.Should().Be(7);
        result.Value.IsAtHeldCap.Should().BeFalse();
        result.Value.IsFrozenToday.Should().BeFalse();
        result.Value.RecentFreezeDates.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_FreezesAvailable_IsMinOfBalanceAndMonthlyRemaining()
    {
        var user = CreateTestUser();
        user.SetStreakState(21, 21, Today);
        user.TryEarnStreakFreezes(); // balance = 3
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        // 1 freeze used this month already → monthlyRemaining = 2.
        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, new DateOnly(Today.Year, Today.Month, 5))
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(freezes.AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.StreakFreezeBalance.Should().Be(3);
        result.Value.FreezesUsedThisMonth.Should().Be(1);
        result.Value.FreezesAvailable.Should().Be(2); // min(balance=3, monthlyRem=2)
        result.Value.IsAtHeldCap.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_FrozenToday_ReportsCorrectly()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var freezes = new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, Today)
        };
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(freezes.AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsFrozenToday.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_ProgressAndDaysUntilNextFreeze_ComputedCorrectly()
    {
        var user = CreateTestUser();
        // CurrentStreak = 10, LastEarnedAtStreak = 7 → delta = 3, progress = 3, daysUntilNext = 4
        user.SetStreakState(7, 7, Today);
        user.TryEarnStreakFreezes(); // balance = 1, last = 7
        user.SetStreakState(10, 10, Today);

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.StreakFreezeBalance.Should().Be(1);
        result.Value.ProgressToNextFreeze.Should().Be(3);
        result.Value.DaysUntilNextFreeze.Should().Be(4);
        result.Value.IsAtHeldCap.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_AtHeldCap_DaysUntilNextFreezeIs7()
    {
        var user = CreateTestUser();
        user.SetStreakState(21, 21, Today);
        user.TryEarnStreakFreezes(); // balance = 3, at cap

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _streakFreezeRepo.FindAsync(
            Arg.Any<Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>().AsReadOnly());

        var query = new GetStreakInfoQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsAtHeldCap.Should().BeTrue();
        result.Value.DaysUntilNextFreeze.Should().Be(7);
    }
}
