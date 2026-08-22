using System.Text.RegularExpressions;
using Orbit.Application.Habits.Queries;

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
    string SurfaceId);

public static partial class MetricsCardBuilder
{
    public const string Directive = "[[orbit:metrics]]";
    public const string ProgressSurfaceId = "progress";

    public const string PromptInstruction = """
        ## Metrics rendering (this client)
        This app can display one overview card for the user's progress metrics. When the user asks how their current week went, asks for their progress or metrics, or asks for a weekly overview, do NOT write the metric values out as text and do NOT provide a per-habit drill-in. Instead reply with a brief one-line intro and then, on its own final line, exactly ONE directive token: [[orbit:metrics]]. The app replaces the directive with the rendered overview and a chip that opens the progress surface. Emit at most one directive, always as the last thing in your reply. For every other kind of question, answer normally and do not emit a directive.
        """;

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
            ProgressSurfaceId);

    [GeneratedRegex(@"\[\[orbit:metrics\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex(@"[\t ]{2,}")]
    private static partial Regex RepeatedHorizontalWhitespaceRegex();
}
