using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Services.Goals;

public class StreakGoalReadSyncerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly StreakGoalReadSyncer _syncer;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public StreakGoalReadSyncerTests()
    {
        _syncer = new StreakGoalReadSyncer(_goalRepo);
    }

    private static Goal CreateBadHabitStreakGoal(decimal target)
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", target, "days", Type: GoalType.Streak)).Value;

        var badHabit = Habit.Create(new HabitCreateParams(
            UserId, "Doom scrolling", FrequencyUnit.Day, 1, IsBadHabit: true, DueDate: Today)).Value;

        SetCreatedAtUtc(badHabit, Today.AddDays(-1));
        badHabit.AddGoal(goal);
        goal.AddHabit(badHabit);
        return goal;
    }

    private static void SetCreatedAtUtc(Habit habit, DateOnly localDate)
    {
        typeof(Habit)
            .GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    private void ArrangeGoals(params Goal[] goals)
    {
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goals.ToList().AsReadOnly());
    }

    [Fact]
    public async Task ComputeFreshValuesAsync_BadHabitStreakGoal_ReturnsQuietDayProgress()
    {
        var goal = CreateBadHabitStreakGoal(target: 7);
        ArrangeGoals(goal);

        var fresh = await _syncer.ComputeFreshValuesAsync(UserId, Today, CancellationToken.None);

        fresh[goal.Id].Should().Be(2);
    }

    [Fact]
    public async Task ComputeFreshValuesAsync_StreakReachesTarget_ReturnsFreshValueWithoutCompletingOrPersisting()
    {
        var goal = CreateBadHabitStreakGoal(target: 2);
        ArrangeGoals(goal);

        var fresh = await _syncer.ComputeFreshValuesAsync(UserId, Today, CancellationToken.None);

        fresh[goal.Id].Should().Be(2);
        goal.Status.Should().Be(GoalStatus.Active);
        goal.CompletedAtUtc.Should().BeNull();
        _goalRepo.DidNotReceive().Update(Arg.Any<Goal>());
    }

    [Fact]
    public async Task ComputeFreshValuesAsync_StreakGoalLostLastHabitWithStaleValue_ReturnsZero()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", 7, "days", Type: GoalType.Streak)).Value;
        typeof(Goal).GetProperty(nameof(Goal.CurrentValue))!.SetValue(goal, 4m);
        ArrangeGoals(goal);

        var fresh = await _syncer.ComputeFreshValuesAsync(UserId, Today, CancellationToken.None);

        fresh[goal.Id].Should().Be(0);
    }

    [Fact]
    public async Task ComputeFreshValuesAsync_NoActiveStreakGoals_ReturnsEmpty()
    {
        ArrangeGoals();

        var fresh = await _syncer.ComputeFreshValuesAsync(UserId, Today, CancellationToken.None);

        fresh.Should().BeEmpty();
    }
}
