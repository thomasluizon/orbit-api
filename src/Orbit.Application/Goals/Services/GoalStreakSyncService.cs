using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Goals.Services;

public static class GoalStreakSyncService
{
    public static bool NeedsPassiveSync(Goal goal, DateOnly userToday)
    {
        if (!IsActiveStreakGoal(goal))
            return false;

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

    public static bool SyncCurrentStreak(Goal goal, DateOnly userToday)
    {
        var currentStreak = CalculateCurrentStreak(goal, userToday);
        if (!currentStreak.HasValue)
            return false;

        goal.SyncStreakProgress(currentStreak.Value);
        return true;
    }

    public static bool SyncCurrentStreakIfNeeded(Goal goal, DateOnly userToday)
    {
        if (!NeedsPassiveSync(goal, userToday))
            return false;

        return SyncCurrentStreak(goal, userToday);
    }

    private static bool IsActiveStreakGoal(Goal goal) =>
        goal.Type == GoalType.Streak && goal.Status == GoalStatus.Active;
}
