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
    DateOnly? LastCompletedDate = null);

public static partial class MetricsCardBuilder
{
    public const string Directive = "[[orbit:metrics]]";
    public const string ProgressSurfaceId = "progress";

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

    public static MetricsCard BuildHabit(HabitMetrics habitMetrics, RetrospectiveMetrics periodMetrics) =>
        new(
            "habit",
            periodMetrics.CompletionRate,
            habitMetrics.TotalCompletions,
            periodMetrics.TotalScheduled,
            periodMetrics.ActiveDays,
            habitMetrics.CurrentStreak,
            habitMetrics.LongestStreak,
            periodMetrics.TotalScheduled > 0 || habitMetrics.TotalCompletions > 0,
            "habit",
            periodMetrics.CompletionSeries,
            HabitId: habitMetrics.HabitId,
            HabitTitle: habitMetrics.HabitTitle,
            WeeklyCompletionRate: habitMetrics.WeeklyCompletionRate,
            MonthlyCompletionRate: habitMetrics.MonthlyCompletionRate,
            LastCompletedDate: habitMetrics.LastCompletedDate);

    [GeneratedRegex(@"\[\[orbit:metrics\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex(@"[\t ]{2,}")]
    private static partial Regex RepeatedHorizontalWhitespaceRegex();
}
