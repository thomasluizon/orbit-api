using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Goals.Services;

public static class GoalMetricsCalculator
{
    public static GoalMetrics Calculate(Goal goal, DateOnly userToday)
    {
        // Progress percentage
        var progressPercentage = goal.TargetValue > 0
            ? Math.Min(100, Math.Round(goal.CurrentValue / goal.TargetValue * 100, 1))
            : 0;

        // Velocity: progress per day since creation
        var creationDate = DateOnly.FromDateTime(goal.CreatedAtUtc);
        var daysSinceCreation = Math.Max(1, userToday.DayNumber - creationDate.DayNumber);
        var velocityPerDay = Math.Round(goal.CurrentValue / daysSinceCreation, 2);

        // Projected completion date
        DateOnly? projectedCompletionDate = null;
        if (velocityPerDay > 0 && goal.CurrentValue < goal.TargetValue)
        {
            var remaining = goal.TargetValue - goal.CurrentValue;
            var daysToComplete = (int)Math.Ceiling(remaining / velocityPerDay);
            projectedCompletionDate = userToday.AddDays(daysToComplete);
        }

        // Days to deadline
        int? daysToDeadline = goal.Deadline.HasValue
            ? goal.Deadline.Value.DayNumber - userToday.DayNumber
            : null;

        // Tracking status
        var trackingStatus = DetermineTrackingStatus(goal, projectedCompletionDate, daysToDeadline);

        // Habit adherence
        var habitAdherence = goal.Habits.Select(h =>
        {
            var metrics = HabitMetricsCalculator.Calculate(h, userToday);
            return new LinkedHabitAdherence(
                h.Id, h.Title,
                metrics.WeeklyCompletionRate,
                metrics.MonthlyCompletionRate,
                metrics.CurrentStreak);
        }).ToList();

        return new GoalMetrics(
            progressPercentage, velocityPerDay, projectedCompletionDate,
            daysToDeadline, trackingStatus, habitAdherence);
    }

    private static string DetermineTrackingStatus(Goal goal, DateOnly? projected, int? daysToDeadline)
    {
        if (goal.Status == GoalStatus.Completed) return "completed";
        if (!goal.Deadline.HasValue) return "no_deadline";
        if (daysToDeadline < 0) return "behind";

        var progress = goal.TargetValue > 0
            ? goal.CurrentValue / goal.TargetValue * 100
            : 0;
        if (daysToDeadline <= 7 && progress < 50) return "at_risk";

        return "on_track";
    }
}
