using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Goals.Services;

public readonly record struct GoalProgressSyncOutcome(bool Synced, bool JustCompleted)
{
    public static readonly GoalProgressSyncOutcome NotSynced = new(false, false);
}

public static class GoalProgressSyncService
{
    public static int? ComputeReadValue(Goal goal, DateOnly userToday)
    {
        if (goal.Status != GoalStatus.Active)
            return null;

        return goal.Type switch
        {
            GoalType.Standard when goal.Habits.Count > 0 => CalculateStandardCompletions(goal),
            GoalType.Streak => GoalStreakSyncService.ComputeReadValue(goal, userToday),
            _ => null
        };
    }

    public static GoalProgressSyncOutcome SyncCurrentProgress(Goal goal, DateOnly userToday)
    {
        if (goal.Status != GoalStatus.Active)
            return GoalProgressSyncOutcome.NotSynced;

        if (goal.Type == GoalType.Streak)
        {
            var streakOutcome = GoalStreakSyncService.SyncCurrentStreak(goal, userToday);
            return new GoalProgressSyncOutcome(streakOutcome.Synced, streakOutcome.JustCompleted);
        }

        if (goal.Habits.Count == 0)
            return GoalProgressSyncOutcome.NotSynced;

        var result = goal.SyncStandardProgress(CalculateStandardCompletions(goal));
        return new GoalProgressSyncOutcome(Synced: result.IsSuccess, JustCompleted: result.IsSuccess && result.Value);
    }

    public static void ApplyReadValue(Goal goal, DateOnly userToday)
    {
        var readValue = ComputeReadValue(goal, userToday);
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
            .SelectMany(habit => habit.Logs)
            .Count(log => !log.IsDeleted && log.Value > 0 && log.CreatedAtUtc >= goal.CreatedAtUtc);
}
