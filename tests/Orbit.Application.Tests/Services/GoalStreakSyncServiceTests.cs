using FluentAssertions;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Services;

public class GoalStreakSyncServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static Goal CreateStreakGoal(decimal target = 7)
    {
        return Goal.Create(new Goal.CreateGoalParams(
            UserId, "Read every day", target, "days", Type: GoalType.Streak)).Value;
    }

    private static Habit CreateDailyHabit(string title, DateOnly createdOn, bool isBadHabit = false)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1, IsBadHabit: isBadHabit, DueDate: Today)).Value;
        SetCreatedAtUtc(habit, createdOn);
        return habit;
    }

    private static void SetCreatedAtUtc(Habit habit, DateOnly localDate)
    {
        typeof(Habit)
            .GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    private static DateOnly SyncedDate(Goal goal) =>
        DateOnly.FromDateTime(goal.StreakSyncedAtUtc!.Value);

    [Fact]
    public void NeedsPassiveSync_NeverSyncedActiveStreakGoalWithHabit_ReturnsTrue()
    {
        var goal = CreateStreakGoal();
        goal.AddHabit(CreateDailyHabit("Meditate", Today.AddDays(-1)));

        GoalStreakSyncService.NeedsPassiveSync(goal, Today).Should().BeTrue();
    }

    [Fact]
    public void NeedsPassiveSync_NonStreakGoal_ReturnsFalse()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, "Save money", 100, "BRL")).Value;

        GoalStreakSyncService.NeedsPassiveSync(goal, Today).Should().BeFalse();
    }

    [Fact]
    public void NeedsPassiveSync_CompletedStreakGoal_ReturnsFalse()
    {
        var goal = CreateStreakGoal();
        goal.MarkCompleted();

        GoalStreakSyncService.NeedsPassiveSync(goal, Today).Should().BeFalse();
    }

    [Fact]
    public void NeedsPassiveSync_SyncedOnUserToday_ReturnsFalse()
    {
        var goal = CreateStreakGoal();
        goal.AddHabit(CreateDailyHabit("Meditate", Today.AddDays(-1)));
        GoalStreakSyncService.SyncCurrentStreak(goal, Today);

        GoalStreakSyncService.NeedsPassiveSync(goal, SyncedDate(goal)).Should().BeFalse();
    }

    [Fact]
    public void NeedsPassiveSync_SyncedBeforeUserToday_ReturnsTrue()
    {
        var goal = CreateStreakGoal();
        goal.AddHabit(CreateDailyHabit("Meditate", Today.AddDays(-1)));
        GoalStreakSyncService.SyncCurrentStreak(goal, Today);

        GoalStreakSyncService.NeedsPassiveSync(goal, SyncedDate(goal).AddDays(1)).Should().BeTrue();
    }

    [Fact]
    public void CalculateCurrentStreak_NonStreakGoal_ReturnsNull()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, "Save money", 100, "BRL")).Value;
        goal.AddHabit(CreateDailyHabit("Meditate", Today.AddDays(-1)));

        GoalStreakSyncService.CalculateCurrentStreak(goal, Today).Should().BeNull();
    }

    [Fact]
    public void CalculateCurrentStreak_NoLinkedHabits_ReturnsNull()
    {
        var goal = CreateStreakGoal();

        GoalStreakSyncService.CalculateCurrentStreak(goal, Today).Should().BeNull();
    }

    [Fact]
    public void CalculateCurrentStreak_TakesMinAcrossLinkedHabits()
    {
        var goal = CreateStreakGoal();

        var twoDayStreakHabit = CreateDailyHabit("Meditate", Today.AddDays(-1));
        twoDayStreakHabit.Log(Today.AddDays(-1));
        twoDayStreakHabit.Log(Today);

        var oneDayStreakHabit = CreateDailyHabit("Stretch", Today.AddDays(-2));
        oneDayStreakHabit.Log(Today);

        goal.AddHabit(twoDayStreakHabit);
        goal.AddHabit(oneDayStreakHabit);

        GoalStreakSyncService.CalculateCurrentStreak(goal, Today).Should().Be(1);
    }

    [Fact]
    public void CalculateCurrentStreak_BadHabitQuietDays_CountsUnloggedDays()
    {
        var goal = CreateStreakGoal();
        goal.AddHabit(CreateDailyHabit("Doom scrolling", Today.AddDays(-2), isBadHabit: true));

        GoalStreakSyncService.CalculateCurrentStreak(goal, Today).Should().Be(3);
    }

    [Fact]
    public void SyncCurrentStreak_UpdatesProgressAndTimestamp_ReturnsTrue()
    {
        var goal = CreateStreakGoal();
        var habit = CreateDailyHabit("Meditate", Today.AddDays(-1));
        habit.Log(Today.AddDays(-1));
        habit.Log(Today);
        goal.AddHabit(habit);

        var synced = GoalStreakSyncService.SyncCurrentStreak(goal, Today);

        synced.Synced.Should().BeTrue();
        goal.CurrentValue.Should().Be(2);
        goal.StreakSyncedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void SyncCurrentStreak_NeverHadHabits_ReturnsFalseAndLeavesGoalUntouched()
    {
        var goal = CreateStreakGoal();

        var synced = GoalStreakSyncService.SyncCurrentStreak(goal, Today);

        synced.Synced.Should().BeFalse();
        goal.CurrentValue.Should().Be(0);
        goal.StreakSyncedAtUtc.Should().BeNull();
    }

    [Fact]
    public void SyncCurrentStreak_ZeroHabitsWithStaleValue_ResetsToZero()
    {
        var goal = CreateStreakGoal();
        SetCurrentValue(goal, 3);

        var resynced = GoalStreakSyncService.SyncCurrentStreak(goal, Today);

        resynced.Synced.Should().BeTrue();
        resynced.JustCompleted.Should().BeFalse();
        goal.CurrentValue.Should().Be(0);
        goal.StreakSyncedAtUtc.Should().BeNull();
    }

    [Fact]
    public void NeedsPassiveSync_ZeroHabitStreakGoalWithStaleValue_ReturnsTrue()
    {
        var goal = CreateStreakGoal();
        SetCurrentValue(goal, 4);

        GoalStreakSyncService.NeedsPassiveSync(goal, Today).Should().BeTrue();
    }

    [Fact]
    public void NeedsPassiveSync_ZeroHabitStreakGoalAlreadyZero_ReturnsFalse()
    {
        var goal = CreateStreakGoal();

        GoalStreakSyncService.NeedsPassiveSync(goal, Today).Should().BeFalse();
    }

    private static void SetCurrentValue(Goal goal, decimal value)
    {
        typeof(Goal)
            .GetProperty(nameof(Goal.CurrentValue))!
            .SetValue(goal, value);
    }

    [Fact]
    public void SyncCurrentStreakIfNeeded_AlreadySyncedOnUserToday_ReturnsFalse()
    {
        var goal = CreateStreakGoal();
        goal.AddHabit(CreateDailyHabit("Meditate", Today.AddDays(-1)));
        GoalStreakSyncService.SyncCurrentStreak(goal, Today);
        var valueAfterFirstSync = goal.CurrentValue;

        var resynced = GoalStreakSyncService.SyncCurrentStreakIfNeeded(goal, SyncedDate(goal));

        resynced.Synced.Should().BeFalse();
        goal.CurrentValue.Should().Be(valueAfterFirstSync);
    }

    [Fact]
    public void SyncCurrentStreakIfNeeded_StaleSync_Resyncs()
    {
        var goal = CreateStreakGoal();
        var habit = CreateDailyHabit("Meditate", Today.AddDays(-1));
        habit.Log(Today.AddDays(-1));
        habit.Log(Today);
        goal.AddHabit(habit);
        GoalStreakSyncService.SyncCurrentStreak(goal, Today.AddDays(-1));

        var resynced = GoalStreakSyncService.SyncCurrentStreakIfNeeded(goal, SyncedDate(goal).AddDays(1));

        resynced.Synced.Should().BeTrue();
        goal.CurrentValue.Should().Be(2);
    }

    [Fact]
    public void SyncCurrentStreakIfNeeded_NeverSynced_Syncs()
    {
        var goal = CreateStreakGoal();
        var habit = CreateDailyHabit("Meditate", Today.AddDays(-1));
        habit.Log(Today);
        goal.AddHabit(habit);

        var synced = GoalStreakSyncService.SyncCurrentStreakIfNeeded(goal, Today);

        synced.Synced.Should().BeTrue();
        goal.CurrentValue.Should().Be(1);
        goal.StreakSyncedAtUtc.Should().NotBeNull();
    }
}
