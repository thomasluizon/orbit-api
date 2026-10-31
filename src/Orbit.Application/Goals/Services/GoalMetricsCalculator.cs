using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Goals.Services;

public static class GoalMetricsCalculator
{
    public static GoalMetrics Calculate(Goal goal, DateOnly userToday, int weekStartDay)
    {
        var progressPercentage = goal.TargetValue > 0
            ? Math.Min(100, Math.Round(goal.CurrentValue / goal.TargetValue * 100, 1))
            : 0;

        var creationDate = DateOnly.FromDateTime(goal.CreatedAtUtc);
        var daysSinceCreation = Math.Max(1, userToday.DayNumber - creationDate.DayNumber);
        var velocityPerDay = Math.Round(goal.CurrentValue / daysSinceCreation, 2);

        DateOnly? projectedCompletionDate = null;
        if (velocityPerDay > 0 && goal.CurrentValue < goal.TargetValue)
        {
            var remaining = goal.TargetValue - goal.CurrentValue;
            var availableDays = DateOnly.MaxValue.DayNumber - userToday.DayNumber;
            if (availableDays > 0 && remaining / availableDays <= velocityPerDay)
            {
                var daysToComplete = Math.Ceiling(remaining / velocityPerDay);
                if (daysToComplete <= availableDays)
                    projectedCompletionDate = userToday.AddDays((int)daysToComplete);
            }
        }

        int? daysToDeadline = goal.Deadline.HasValue
            ? goal.Deadline.Value.DayNumber - userToday.DayNumber
            : null;

        var trackingStatus = DetermineTrackingStatus(goal, daysToDeadline);

        var habitAdherence = goal.Habits.Select(h =>
        {
            var metrics = HabitMetricsCalculator.Calculate(h, userToday, weekStartDay);
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

    public static string GetTrackingStatus(Goal goal, DateOnly userToday) =>
        DetermineTrackingStatus(goal, goal.Deadline?.DayNumber - userToday.DayNumber);

    private static string DetermineTrackingStatus(Goal goal, int? daysToDeadline)
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
