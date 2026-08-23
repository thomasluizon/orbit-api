using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Services.Goals;

public class GoalProgressReadSyncerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly GoalProgressReadSyncer _syncer;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public GoalProgressReadSyncerTests()
    {
        _syncer = new GoalProgressReadSyncer(_goalRepo);
    }

    private static Goal CreateBadHabitStreakGoal(decimal target)
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", target, "days", Type: GoalType.Streak)).Value;

        var badHabit = Habit.Create(new HabitCreateParams(
            UserId, "Doom scrolling", FrequencyUnit.Day, 1,
            IsBadHabit: true, DueDate: Today.AddDays(-1))).Value;

        badHabit.AddGoal(goal);
        return goal;
    }

    private void ArrangeGoals(params Goal[] goals)
    {
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(goals.ToList().AsReadOnly());
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
    public async Task ComputeFreshValuesAsync_LinkedStandardGoal_ReturnsCompletionsSinceGoalStarted()
    {
        var goal = Goal.Create(UserId, "Complete 10 sessions", 10, "sessions").Value;
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Exercise", FrequencyUnit.Day, 2, DueDate: Today, IsFlexible: true)).Value;
        habit.Log(Today);
        habit.Log(Today);
        goal.AddHabit(habit);
        ArrangeGoals(goal);

        var fresh = await _syncer.ComputeFreshValuesAsync(UserId, Today, CancellationToken.None);

        fresh[goal.Id].Should().Be(2);
        goal.Status.Should().Be(GoalStatus.Active);
        _goalRepo.DidNotReceive().Update(Arg.Any<Goal>());
    }

    [Fact]
    public void CalculateStandardCompletions_ExcludesLogsCreatedBeforeGoalStarted()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Exercise", FrequencyUnit.Day, 2, DueDate: Today, IsFlexible: true)).Value;
        habit.Log(Today);
        var goal = Goal.Create(UserId, "Complete 10 sessions", 10, "sessions").Value;
        goal.AddHabit(habit);
        habit.Log(Today);

        var completions = GoalProgressSyncService.CalculateStandardCompletions(goal);

        completions.Should().Be(1);
    }

    [Fact]
    public void CalculateStandardCompletions_ExcludesSoftDeletedLinkedHabits()
    {
        var goal = Goal.Create(UserId, "Complete 10 sessions", 10, "sessions").Value;
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Exercise", FrequencyUnit.Day, 2, DueDate: Today, IsFlexible: true)).Value;
        goal.AddHabit(habit);
        habit.Log(Today);
        habit.SoftDelete(new DateTime(2026, 3, 20, 10, 0, 0, DateTimeKind.Utc));

        var completions = GoalProgressSyncService.CalculateStandardCompletions(goal);

        completions.Should().Be(0);
    }

    [Fact]
    public async Task ComputeFreshValuesAsync_NoActiveStreakGoals_ReturnsEmpty()
    {
        ArrangeGoals();

        var fresh = await _syncer.ComputeFreshValuesAsync(UserId, Today, CancellationToken.None);

        fresh.Should().BeEmpty();
    }
}
