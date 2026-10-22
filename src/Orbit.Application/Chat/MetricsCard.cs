using System.Text.RegularExpressions;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public record MetricsCard(
    string Period,
    int CompletionRate,
    int TotalCompletions,
    int TotalScheduled,
    int ActiveDays,
    int CurrentStreak,
    int BestStreak,
    bool HasData,
    string SurfaceId,
    CompletionSeries? Series = null,
    string? TopHabitName = null,
    string? TopHabitEmoji = null,
    Guid? HabitId = null,
    string? HabitTitle = null,
    decimal? WeeklyCompletionRate = null,
    decimal? MonthlyCompletionRate = null,
    DateOnly? LastCompletedDate = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PeriodHabitMetrics? PeriodMetrics = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ReadBlockMetadata? Read = null);

public static partial class MetricsCardBuilder
{
    public const string Directive = "[[orbit:metrics]]";
    public const string ProgressSurfaceId = "progress";

    public const string PartsPromptInstruction = "When the user asks about their current week, place [[orbit:metrics]] on its own line where the progress block belongs. Never repeat its figures in text.";
    public const string HabitPartsPromptInstruction = "After get_habit_metrics succeeds, place [[orbit:metrics]] on its own line where that habit's metrics block belongs. Never repeat its figures in text.";

    public const string PromptInstruction = """
        ## Metrics rendering (this client)
        This app can display one card for current week progress. When the user asks about their current week, write one short intro without repeating figures, then emit [[orbit:metrics]] before optional follow-ups. Emit at most one directive. For other questions, answer normally.
        """;

    public const string HabitPromptInstruction = "After get_habit_metrics succeeds, write one short line without repeating figures, then emit [[orbit:metrics]] before optional follow-ups so the card shows that habit.";

    public static bool TryExtractDirective(string? message, out string stripped)
    {
        stripped = message ?? string.Empty;
        if (string.IsNullOrEmpty(message))
            return false;

        if (!DirectiveRegex().IsMatch(message))
            return false;

        stripped = RepeatedHorizontalWhitespaceRegex()
            .Replace(DirectiveRegex().Replace(message, " "), " ")
            .Trim();
        return true;
    }

    public static MetricsCard Build(string period, RetrospectiveMetrics metrics) =>
        new(
            period,
            metrics.CompletionRate,
            metrics.TotalCompletions,
            metrics.TotalScheduled,
            metrics.ActiveDays,
            metrics.CurrentStreak,
            metrics.BestStreak,
            metrics.TotalScheduled > 0 || metrics.TotalCompletions > 0 || metrics.ActiveDays > 0 || metrics.BadHabitSlips > 0,
            ProgressSurfaceId,
            metrics.CompletionSeries,
            metrics.TopHabits.FirstOrDefault()?.Name,
            metrics.TopHabits.FirstOrDefault()?.Emoji);

    public static MetricsCard BuildPeriod(PeriodTotalCard total)
    {
        var metrics = total.Metrics;
        return new("week", (int)Math.Round(metrics.CompletionRate ?? 0), total.Value,
            metrics.ScheduledDates, metrics.DistinctLoggedDates, metrics.CurrentStreak, metrics.BestStreak,
            total.Value > 0 || metrics.ScheduledDates > 0, "progress", total.Series, PeriodMetrics: metrics);
    }

    public static MetricsCard BuildHabit(HabitMetrics metrics) => new(
        "habit", (int)Math.Round(metrics.Period?.CompletionRate ?? 0), metrics.TotalCompletions,
        metrics.Period?.ScheduledDates ?? 0, metrics.Period?.DistinctLoggedDates ?? 0,
        metrics.CurrentStreak, metrics.LongestStreak, metrics.TotalCompletions > 0 || metrics.Period?.ScheduledDates > 0,
        "habit", metrics.PeriodSeries is { } series ? new CompletionSeries(series.Granularity,
            series.Points.Select(p => new CompletionSeriesPoint(p.StartDate, p.EndDate, p.Scheduled, p.Completed, p.CompletionRate)).ToList()) : null,
        HabitId: metrics.HabitId, HabitTitle: metrics.HabitTitle,
        WeeklyCompletionRate: metrics.WeeklyCompletionRate, MonthlyCompletionRate: metrics.MonthlyCompletionRate,
        LastCompletedDate: metrics.LastCompletedDate, PeriodMetrics: metrics.Period);

    [GeneratedRegex(@"\[\[orbit:metrics\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex(@"[\t ]{2,}")]
    private static partial Regex RepeatedHorizontalWhitespaceRegex();
}
