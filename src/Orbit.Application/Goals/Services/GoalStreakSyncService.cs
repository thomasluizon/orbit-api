using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Goals.Services;

/// <summary>
/// Result of a streak-goal sync attempt. <see cref="Synced"/> reports whether the goal's
/// progress was recomputed and changed; <see cref="JustCompleted"/> reports whether that sync
/// transitioned the goal from Active to Completed, so write-side callers can fire gamification once.
/// </summary>
public readonly record struct StreakSyncOutcome(bool Synced, bool JustCompleted)
{
    public static readonly StreakSyncOutcome NotSynced = new(false, false);
}

public static class GoalStreakSyncService
{
    public static bool NeedsPassiveSync(Goal goal, DateOnly userToday)
    {
        if (!IsActiveStreakGoal(goal))
            return false;

        if (goal.Habits.Count == 0)
            return goal.CurrentValue != 0;

        var syncedDate = goal.StreakSyncedAtUtc.HasValue
            ? DateOnly.FromDateTime(goal.StreakSyncedAtUtc.Value)
            : (DateOnly?)null;

        return syncedDate is null || syncedDate < userToday;
    }

    public static int? CalculateCurrentStreak(Goal goal, DateOnly userToday)
    {
        if (!IsActiveStreakGoal(goal))
            return null;

        var linkedHabits = goal.Habits.ToList();
        if (linkedHabits.Count == 0)
            return null;

        return linkedHabits
            .Select(habit => HabitMetricsCalculator.Calculate(habit, userToday).CurrentStreak)
            .Min();
    }

    /// <summary>
    /// Computes the value an active streak goal should display right now from its linked habits'
    /// logs, without mutating or persisting anything. Returns 0 for a streak goal that has lost all
    /// its habits (its retained value is stale), the minimum live streak across linked habits, or
    /// null when the goal is not an active streak goal (the caller keeps its current value). Read
    /// paths use this to surface the fresh streak while leaving completion to the write paths.
    /// </summary>
    public static int? ComputeReadValue(Goal goal, DateOnly userToday)
    {
        if (!IsActiveStreakGoal(goal))
            return null;

        if (goal.Habits.Count == 0)
            return 0;

        return CalculateCurrentStreak(goal, userToday);
    }

    public static StreakSyncOutcome SyncCurrentStreak(Goal goal, DateOnly userToday)
    {
        if (IsActiveStreakGoal(goal) && goal.Habits.Count == 0)
            return new StreakSyncOutcome(Synced: goal.ResetStreakProgress(), JustCompleted: false);

        var currentStreak = CalculateCurrentStreak(goal, userToday);
        if (!currentStreak.HasValue)
            return StreakSyncOutcome.NotSynced;

        var result = goal.SyncStreakProgress(currentStreak.Value);
        return new StreakSyncOutcome(Synced: true, JustCompleted: result.IsSuccess && result.Value);
    }

    public static StreakSyncOutcome SyncCurrentStreakIfNeeded(Goal goal, DateOnly userToday)
    {
        if (!NeedsPassiveSync(goal, userToday))
            return StreakSyncOutcome.NotSynced;

        return SyncCurrentStreak(goal, userToday);
    }

    /// <summary>
    /// Applies the freshly computed read value to a non-persisted active streak goal so its DTO and
    /// metrics reflect the live streak, without ever flipping Status. No-ops when the goal is not an
    /// active streak goal. The caller must pass an instance that will not be saved — completion and
    /// gamification stay solely on the write paths and the hosted sweep.
    /// </summary>
    public static void ApplyReadValue(Goal goal, DateOnly userToday)
    {
        var readValue = ComputeReadValue(goal, userToday);
        if (readValue.HasValue)
            goal.SyncStreakProgress(readValue.Value, allowCompletion: false);
    }

    private static bool IsActiveStreakGoal(Goal goal) =>
        goal.Type == GoalType.Streak && goal.Status == GoalStatus.Active;
}
