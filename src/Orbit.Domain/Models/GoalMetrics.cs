namespace Orbit.Domain.Models;

public record GoalMetrics(
    decimal ProgressPercentage,
    decimal VelocityPerDay,
    DateOnly? ProjectedCompletionDate,
    int? DaysToDeadline,
    string TrackingStatus,
    List<LinkedHabitAdherence> HabitAdherence);

public record LinkedHabitAdherence(
    Guid HabitId,
    string HabitTitle,
    decimal WeeklyCompletionRate,
    decimal MonthlyCompletionRate,
    int CurrentStreak);
