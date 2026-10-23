namespace Orbit.Domain.Models;

public record HabitMetrics(
    int CurrentStreak,
    int LongestStreak,
    decimal WeeklyCompletionRate,
    decimal MonthlyCompletionRate,
    int TotalCompletions,
    DateOnly? LastCompletedDate,
    Guid? HabitId = null,
    string? HabitTitle = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PeriodHabitMetrics? Period = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SourceRevision = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PeriodMetricSeries? PeriodSeries = null);
