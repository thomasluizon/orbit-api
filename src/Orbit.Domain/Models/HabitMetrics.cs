namespace Orbit.Domain.Models;

public record HabitMetrics(
    int CurrentStreak,
    int LongestStreak,
    decimal WeeklyCompletionRate,
    decimal MonthlyCompletionRate,
    int TotalCompletions,
    DateOnly? LastCompletedDate);
