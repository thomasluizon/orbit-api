using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Goals.Services;

public readonly record struct GoalProgressSyncOutcome(bool Synced, bool JustCompleted)
{
    public static readonly GoalProgressSyncOutcome NotSynced = new(false, false);
}

public static class GoalProgressSyncService
{
    public static int? ComputeReadValue(Goal goal, DateOnly userToday, int weekStartDay)
    {
        if (goal.Status != GoalStatus.Active)
            return null;

        return goal.Type switch
        {
            GoalType.Standard when goal.HasActiveLinkedHabits => CalculateStandardCompletions(goal),
            GoalType.Streak => GoalStreakSyncService.ComputeReadValue(goal, userToday, weekStartDay),
            _ => null
        };
    }

    public static GoalProgressSyncOutcome SyncCurrentProgress(Goal goal, DateOnly userToday, int weekStartDay)
    {
        if (goal.Status != GoalStatus.Active)
            return GoalProgressSyncOutcome.NotSynced;

        if (goal.Type == GoalType.Streak)
        {
            var streakOutcome = GoalStreakSyncService.SyncCurrentStreak(goal, userToday, weekStartDay);
            return new GoalProgressSyncOutcome(streakOutcome.Synced, streakOutcome.JustCompleted);
        }

        if (!goal.HasActiveLinkedHabits)
            return GoalProgressSyncOutcome.NotSynced;

        var result = goal.SyncStandardProgress(CalculateStandardCompletions(goal));
        return new GoalProgressSyncOutcome(Synced: result.IsSuccess, JustCompleted: result.IsSuccess && result.Value);
    }

    public static void ApplyReadValue(Goal goal, DateOnly userToday, int weekStartDay)
    {
        var readValue = ComputeReadValue(goal, userToday, weekStartDay);
        if (!readValue.HasValue)
            return;

        ApplyReadValue(goal, readValue.Value);
    }

    public static void ApplyReadValue(Goal goal, int readValue)
    {
        if (goal.Type == GoalType.Streak)
            goal.SyncStreakProgress(readValue, allowCompletion: false);
        else
            goal.SyncStandardProgress(readValue, allowCompletion: false);
    }

    public static int CalculateStandardCompletions(Goal goal) =>
        goal.Habits
            .Where(habit => !habit.IsDeleted)
            .SelectMany(habit => habit.Logs)
            .Count(log => !log.IsDeleted && log.Value > 0 && log.CreatedAtUtc >= goal.CreatedAtUtc);
}
