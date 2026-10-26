using Orbit.Application.Common;

namespace Orbit.Application.Habits.Queries;

/// <summary>
/// Resolves a retrospective period string into a [DateFrom, DateTo] window. The "week" period
/// anchors on the user's <c>WeekStartDay</c> (0 = Sunday, 1 = Monday) via <see cref="WeekMath"/>
/// so the server window matches the client's WeekStartDay-driven calendars. Legacy longer periods
/// retain rolling spans; relative selectors distinguish calendar periods from rolling windows.
/// </summary>
public static class RetrospectivePeriodRange
{
    private static readonly string[] Known = ["week", "month", "quarter", "semester", "year"];
    public static IReadOnlyList<string> RelativePeriods { get; } = Array.AsReadOnly(new[]
    {
        "today", "yesterday", "this_week", "last_week", "this_month", "last_month", "last_7_days",
        "last_n_days", "last_n_weeks"
    });

    public static bool IsRelativePeriod(string? period) =>
        period is not null && RelativePeriods.Contains(period, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="period"/> is one <see cref="Resolve"/> understands (case-insensitive),
    /// so callers can reject unknown periods at the trust boundary before resolving.
    /// </summary>
    public static bool IsKnownPeriod(string? period) =>
        period is not null && (Known.Contains(period, StringComparer.OrdinalIgnoreCase) || IsRelativePeriod(period));

    public static (DateOnly DateFrom, DateOnly DateTo) Resolve(string period, DateOnly today, int weekStartDay,
        int? periodCount = null)
    {
        ArgumentNullException.ThrowIfNull(period);
        var normalized = period.ToLowerInvariant();
        if (normalized is "last_n_days" or "last_n_weeks")
        {
            var maxCount = normalized == "last_n_weeks" ? 522 : 3659;
            if (periodCount is null or < 1 || periodCount > maxCount)
                throw new ArgumentOutOfRangeException(nameof(periodCount), "A bounded positive period_count is required.");
            var days = periodCount.Value * (normalized == "last_n_weeks" ? 7 : 1);
            return (today.AddDays(1 - days), today);
        }
        if (periodCount.HasValue)
            throw new ArgumentException("period_count requires last_n_days or last_n_weeks.", nameof(periodCount));
        if (normalized == "yesterday") return (today.AddDays(-1), today.AddDays(-1));
        if (normalized == "last_week")
        {
            var start = WeekMath.WeekStart(today, weekStartDay);
            return (start.AddDays(-7), start.AddDays(-1));
        }
        if (normalized == "last_month")
        {
            var start = new DateOnly(today.Year, today.Month, 1);
            return (start.AddMonths(-1), start.AddDays(-1));
        }
        var dateFrom = normalized switch
        {
            "today" => today,
            "week" or "this_week" => WeekMath.WeekStart(today, weekStartDay),
            "this_month" => new DateOnly(today.Year, today.Month, 1),
            "last_7_days" => today.AddDays(-6),
            "month" => today.AddDays(-29),
            "quarter" => today.AddDays(-89),
            "semester" => today.AddDays(-179),
            "year" => today.AddDays(-364),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown retrospective period.")
        };

        return (dateFrom, today);
    }
}
