using System.Globalization;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat;

public static class RelativeReadPeriod
{
    public const string Instruction = "For every relative period phrase in English or Portuguese on a read, use relative_period and let the server resolve the person's local dates and week start. "
        + "For these reads, use absolute dates only when the person names dates. Never calculate calendar boundaries yourself. "
        + "Map this week / esta semana to this_week, last week / semana passada to last_week, "
        + "this month / este mês to this_month, last month / mês passado to last_month, "
        + "today / hoje to today, yesterday / ontem to yesterday, last 7 days / últimos 7 dias to last_7_days. "
        + "For last N days or weeks / últimos N dias ou últimas N semanas use last_n_days or last_n_weeks with period_count=N. "
        + "For comparisons use baseline_relative_period, with baseline_period_count when counted. "
        + "Ranking rolling_months and complete_months already resolve relative month counts on the server. "
        + "Account streak and goal reads are current snapshots, not period totals; use period analytics or habit metrics for period-scoped habit streaks.";

    public static object PeriodSchema() => new
    {
        type = JsonSchemaTypes.String,
        @enum = RetrospectivePeriodRange.RelativePeriods,
        description = "Use for relative phrases in any language. The server resolves local dates and week start. Overrides the corresponding absolute dates. Current calendar periods end today; previous periods are complete."
    };

    public static object CountSchema() => new
    {
        type = JsonSchemaTypes.Integer,
        minimum = 1,
        maximum = 3659,
        description = "Required only for last_n_days or last_n_weeks. Weeks are rolling seven-day spans, maximum 522 weeks."
    };

    public static object DaySchema() => new
    {
        type = JsonSchemaTypes.String,
        @enum = new[] { "today", "yesterday" },
        description = "Resolve a single relative local day on the server. Overrides date."
    };

    public static bool Supports(string tool) => tool is "get_period_analytics" or "get_retrospective"
        or "get_daily_summary" or "get_habit_metrics" or "get_habit_history" or "get_habit_month"
        or "get_calendar_overview" or "get_habit_ranking" or "query_habits";

    public static async Task ResolveAsync(string tool, JsonObject node, DateOnly today, IUserDateService dates,
        Guid userId, CancellationToken ct)
    {
        if (!Supports(tool)) return;
        if (tool == "query_habits")
        {
            ResolveDay(node, today);
            return;
        }
        var weekStartDay = await dates.GetUserWeekStartDayAsync(userId, ct);
        if (tool == "get_retrospective" && node["relative_period"]?.GetValue<string>() is { } period)
            SetRetrospectivePeriod(node, period);
        ResolveWindow(node, "relative_period", "period_count", "date_from",
            tool == "get_habit_ranking" ? "date_to_exclusive" : "date_to", today, weekStartDay,
            exclusive: tool == "get_habit_ranking");
        if (tool == "get_period_analytics")
            ResolveWindow(node, "baseline_relative_period", "baseline_period_count", "baseline_from", "baseline_to",
                today, weekStartDay);
    }

    private static void ResolveDay(JsonObject node, DateOnly today)
    {
        if (node["relative_period"] is null) return;
        var period = node["relative_period"]!.GetValue<string>().ToLowerInvariant();
        if (period is not ("today" or "yesterday"))
            throw new ArgumentException("A dated habit list requires today or yesterday, not a period range.");
        var (date, _) = RetrospectivePeriodRange.Resolve(period, today, 1);
        node["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (period == "today") node["include_overdue"] ??= true;
        node.Remove("relative_period");
    }

    private static void SetRetrospectivePeriod(JsonObject node, string period)
    {
        switch (period.ToLowerInvariant())
        {
            case "this_month":
            case "last_month":
                node["period"] = "month";
                break;
            case "this_week":
            case "last_week":
            case "last_7_days":
            case "last_n_weeks":
                node["period"] = "week";
                break;
        }
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
        var count = node[countKey]?.GetValue<int>();
        var validation = new RelativeReadPeriodSelectionValidator().Validate(new RelativeReadPeriodSelection(period, count));
        if (!validation.IsValid)
            throw new ArgumentException("A supported relative period and a valid period_count are required.");
        if (exclusive && (node["query_token"] is not null || node["cursor"] is not null || node["window"] is not null || node["months"] is not null))
            throw new ArgumentException("A relative ranking period cannot override a bound query or month window.");
        var (from, to) = RetrospectivePeriodRange.Resolve(period, today, weekStartDay, count);
        node[fromKey] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        node[toKey] = (exclusive ? to.AddDays(1) : to).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        node.Remove(selector);
        node.Remove(countKey);
        if (node.ContainsKey("window_from_utc")) node.Remove("window_from_utc");
        if (node.ContainsKey("window_to_utc")) node.Remove("window_to_utc");
    }
}
