using System.Globalization;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat;

public static class RelativeReadPeriod
{
    public static object Schema => new
    {
        type = JsonSchemaTypes.String,
        @enum = RetrospectivePeriodRange.RelativePeriods,
        description = "Use for relative phrases in any language. The server resolves local dates and week start. Overrides date_from/date_to. Current calendar periods end today; previous periods are complete."
    };

    public static object CountSchema => new
    {
        type = JsonSchemaTypes.Integer,
        minimum = 1,
        maximum = 3659,
        description = "Required only for last_n_days or last_n_weeks. Weeks are rolling seven-day spans, maximum 522 weeks."
    };

    public static bool Supports(string tool) => tool is "get_period_analytics" or "get_retrospective"
        or "get_daily_summary" or "get_habit_metrics" or "get_habit_history" or "get_habit_month"
        or "get_calendar_overview" or "get_habit_ranking";

    public static async Task ResolveAsync(string tool, JsonObject node, DateOnly today, IUserDateService dates,
        Guid userId, CancellationToken ct)
    {
        if (!Supports(tool)) return;
        var weekStartDay = await dates.GetUserWeekStartDayAsync(userId, ct);
        ResolveWindow(node, "relative_period", "period_count", "date_from",
            tool == "get_habit_ranking" ? "date_to_exclusive" : "date_to", today, weekStartDay,
            exclusive: tool == "get_habit_ranking");
        if (tool == "get_period_analytics")
            ResolveWindow(node, "baseline_relative_period", "baseline_period_count", "baseline_from", "baseline_to",
                today, weekStartDay);
    }

    private static void ResolveWindow(JsonObject node, string selector, string countKey, string fromKey, string toKey,
        DateOnly today, int weekStartDay, bool exclusive = false)
    {
        if (node[selector] is null)
        {
            if (node[countKey] is not null) throw new ArgumentException("A relative period is required with a period count.");
            return;
        }
        var period = node[selector]!.GetValue<string>();
        if (!RetrospectivePeriodRange.IsRelativePeriod(period))
            throw new ArgumentException("Unknown relative period.");
        if (exclusive && (node["query_token"] is not null || node["cursor"] is not null || node["window"] is not null || node["months"] is not null))
            throw new ArgumentException("A relative ranking period cannot override a bound query or month window.");
        var (from, to) = RetrospectivePeriodRange.Resolve(period, today, weekStartDay, node[countKey]?.GetValue<int>());
        node[fromKey] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        node[toKey] = (exclusive ? to.AddDays(1) : to).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        node.Remove(selector);
        node.Remove(countKey);
        if (node.ContainsKey("window_from_utc")) node.Remove("window_from_utc");
        if (node.ContainsKey("window_to_utc")) node.Remove("window_to_utc");
    }
}
