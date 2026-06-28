using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetStreakHistoryQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<StreakFreeze> _freezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IFeatureFlagService _featureFlags = Substitute.For<IFeatureFlagService>();
    private readonly GetStreakHistoryQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public GetStreakHistoryQueryHandlerTests()
    {
        _handler = new GetStreakHistoryQueryHandler(_userRepo, _habitRepo, _freezeRepo, _featureFlags);
        _featureFlags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        _freezeRepo.FindAsync(Arg.Any<Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>());
    }

    private static User CreateProUser()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeSubscription("sub", DateTime.UtcNow.AddYears(1));
        return user;
    }

    private static User CreateFreeUser()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    private static Habit CreateDailyHabitWithStreak(int days)
    {
        var startDate = Today.AddDays(-(days - 1));
        var habit = Habit.Create(new HabitCreateParams(UserId, "Daily", FrequencyUnit.Day, 1, DueDate: startDate)).Value;
        typeof(Habit).GetProperty("CreatedAtUtc")!.SetValue(habit, startDate.ToDateTime(TimeOnly.MinValue));
        for (var i = 0; i < days; i++)
            habit.Log(startDate.AddDays(i));
        return habit;
    }

    private void ArrangeHabits(params Habit[] habits)
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.ToList());
    }

    [Fact]
    public async Task Handle_ProUser_RecomputesIncrementingStreakForConsecutiveDays()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateProUser());
        ArrangeHabits(CreateDailyHabitWithStreak(3));

        var query = new GetStreakHistoryQuery(UserId, Today.AddDays(-2), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Points.Should().HaveCount(3);
        result.Value.Points.Select(p => p.Streak).Should().Equal(1, 2, 3);
        result.Value.Points[2].Date.Should().Be(Today);
    }

    [Fact]
    public async Task Handle_FreeUser_ReturnsPayGateFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateFreeUser());

        var query = new GetStreakHistoryQuery(UserId, Today.AddDays(-2), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Domain.Common.Result.PayGateErrorCode);
    }
}
