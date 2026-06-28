using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetHabitsCompletionTrendsQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IFeatureFlagService _featureFlags = Substitute.For<IFeatureFlagService>();
    private readonly GetHabitsCompletionTrendsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public GetHabitsCompletionTrendsQueryHandlerTests()
    {
        _handler = new GetHabitsCompletionTrendsQueryHandler(_userRepo, _habitRepo, _featureFlags);
        _featureFlags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
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

    private static Habit CreateDailyHabit()
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Daily", FrequencyUnit.Day, 1, DueDate: Today.AddDays(-10))).Value;
        typeof(Habit).GetProperty("CreatedAtUtc")!.SetValue(habit, Today.AddDays(-10).ToDateTime(TimeOnly.MinValue));
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
    public async Task Handle_ProUser_ReturnsPerDayCompletionCountAndRate()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateProUser());

        var habit1 = CreateDailyHabit();
        habit1.Log(Today.AddDays(-1));
        habit1.Log(Today);
        var habit2 = CreateDailyHabit();
        habit2.Log(Today);
        ArrangeHabits(habit1, habit2);

        var query = new GetHabitsCompletionTrendsQuery(UserId, Today.AddDays(-1), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ActiveHabitCount.Should().Be(2);
        result.Value.Points.Should().HaveCount(2);
        result.Value.Points[0].Date.Should().Be(Today.AddDays(-1));
        result.Value.Points[0].CompletedCount.Should().Be(1);
        result.Value.Points[0].CompletionRate.Should().Be(50m);
        result.Value.Points[1].Date.Should().Be(Today);
        result.Value.Points[1].CompletedCount.Should().Be(2);
        result.Value.Points[1].CompletionRate.Should().Be(100m);
    }

    [Fact]
    public async Task Handle_FreeUser_ReturnsPayGateFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateFreeUser());

        var query = new GetHabitsCompletionTrendsQuery(UserId, Today.AddDays(-1), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Domain.Common.Result.PayGateErrorCode);
    }
}
