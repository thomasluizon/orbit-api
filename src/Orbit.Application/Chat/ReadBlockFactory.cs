using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Profile.Queries;
using Orbit.Application.Referrals.Queries;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed class ReadBlockFactory(IUserDateService dates, IReadQueryTokenService tokens, IGenericRepository<User> users)
{
    public static bool Supports(string tool) => tool is "query_habits" or "query_goals" or "review_goals"
        or "get_habit_metrics" or "get_period_analytics" or "get_habit_history" or "get_daily_summary"
        or "get_retrospective" or "get_habit_month" or "get_calendar_overview" or "get_gamification_overview"
        or "get_notifications" or "list_tags" or "get_checklist_templates" or "get_api_keys"
        or "get_profile" or "get_subscription_overview" or "get_referral_overview";

    public async Task<JsonElement> ResolveAsync(string tool, JsonElement args, Guid userId, CancellationToken ct)
    {
        var node = JsonNode.Parse(args.GetRawText())!.AsObject();
        ValidateDates(args);
        var today = await dates.GetUserTodayAsync(userId, ct);
        string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (tool == "query_habits" && node["date"]?.GetValue<string>() is { } date)
        {
            if (date.Equals("today", StringComparison.OrdinalIgnoreCase))
            {
                node["date"] = Iso(today);
                node["include_overdue"] ??= true;
            }
        }
        if (tool is "get_daily_summary" or "get_habit_metrics" or "get_retrospective")
        {
            var from = tool == "get_habit_metrics" ? today.AddDays(-29) : today;
            var to = today;
            if (tool == "get_retrospective")
                (from, to) = RetrospectivePeriodRange.Resolve(node["period"]?.GetValue<string>() ?? "week", today,
                    await dates.GetUserWeekStartDayAsync(userId, ct));
            node["date_from"] ??= Iso(from);
            node["date_to"] ??= tool == "get_daily_summary" ? node["date_from"]!.DeepClone() : Iso(to);
        }
        ResolveHabitMonth(tool, node, args, today);
        ApplyDefaults(tool, node);
        if (tool is "get_daily_summary" or "get_retrospective")
        {
            var user = await users.GetByIdAsync(userId, ct);
            node["language"] = !string.IsNullOrWhiteSpace(user?.Language)
                ? user.Language : node["language"]?.GetValue<string>() ?? "en";
        }
        if (tool is "query_goals" or "review_goals" or "get_gamification_overview" or "get_calendar_overview" or "get_habit_metrics")
            node["as_of"] ??= Iso(today);
        if (tool == "get_calendar_overview")
        {
            var user = await users.GetByIdAsync(userId, ct);
            if (!node.ContainsKey("calendar_ids")) node["calendar_ids"] = JsonSerializer.SerializeToNode(user?.GetSelectedCalendarIds());
            var zone = await dates.GetUserTimeZoneAsync(userId, ct);
            if (node["date_to"] is not null) node["date_from"] ??= Iso(today);
            if (node["date_from"]?.GetValue<string>() is { } fromText && DateOnly.TryParseExact(fromText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate))
            {
                var toDate = JsonArgumentParser.ParseDateOnly(args, "date_to") ?? fromDate;
                node["window_from_utc"] ??= TimeZoneInfo.ConvertTimeToUtc(fromDate.ToDateTime(TimeOnly.MinValue), zone).ToString("O", CultureInfo.InvariantCulture);
                node["window_to_utc"] ??= TimeZoneInfo.ConvertTimeToUtc(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue), zone).ToString("O", CultureInfo.InvariantCulture);
            }
            else
            {
                var fromAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
                node["window_from_utc"] ??= fromAtUtc.ToString("O", CultureInfo.InvariantCulture);
                node["window_to_utc"] ??= fromAtUtc.AddDays(60).ToString("O", CultureInfo.InvariantCulture);
            }
        }
        return JsonSerializer.SerializeToElement(node);
    }

    private static void ResolveHabitMonth(string tool, JsonObject node, JsonElement args, DateOnly today)
    {
        if (tool != "get_habit_month") return;
        var window = HabitCalendarWindow.Resolve(JsonArgumentParser.ParseDateOnly(args, "date_from"),
            JsonArgumentParser.ParseDateOnly(args, "date_to"), today);
        if (window.IsFailure) throw new ArgumentException(window.Error);
        node["date_from"] = window.Value.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        node["date_to"] = window.Value.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        node["language"] ??= "en";
    }

    private static void ValidateDates(JsonElement args)
    {
        foreach (var key in new[] { "date", "date_from", "date_to", "baseline_from", "baseline_to", "as_of" })
        {
            if (!args.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Read dates must be strings.");
            var text = value.GetString();
            if (key == "date" && string.Equals(text, "today", StringComparison.OrdinalIgnoreCase)) continue;
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("Read dates must be absolute ISO dates.");
        }
    }

    private static void ApplyDefaults(string tool, JsonObject node)
    {
        if (tool is "query_habits" or "query_goals" or "get_habit_history" or "get_notifications" or "review_goals")
        {
            node["offset"] ??= 0;
            node["limit"] ??= tool is "query_habits" or "query_goals" ? 50 : tool == "get_notifications" ? 10 : 20;
        }
        if (tool == "query_habits")
        {
            node["include_overdue"] ??= false;
            node["include_sub_habits"] ??= true;
            node["include_metrics"] ??= false;
        }
        if (tool == "query_goals") node["include_completed"] ??= false;
        if (tool == "get_calendar_overview")
        {
            node["include_events"] ??= true; node["include_calendars"] ??= true;
            node["include_auto_sync_state"] ??= true; node["include_suggestions"] ??= true;
        }
    }

    public QueryReadBlock? Capture(string tool, JsonElement args, Guid userId, object? payload,
        ReadQueryTicket? previous = null, string? revisionOverride = null)
    {
        var offset = JsonArgumentParser.GetOptionalInt(args, "offset") ?? 0;
        var projection = previous?.Kind == "metrics" && payload is PeriodAnalyticsResponse analytics
            ? new Projection("metrics", MetricsCardBuilder.BuildPeriod(analytics.Total), analytics.SourceRevision)
            : Project(tool, payload, userId, offset);
        if (projection?.Block is null) return null;
        var revision = revisionOverride ?? projection.Revision ?? ReadRevision.Hash(payload);
        var ticket = new ReadQueryTicket(userId, previous?.QueryId ?? Guid.NewGuid(), tool, args.Clone(), projection.Kind,
            revision, previous?.ExpiresAtUtc ?? TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(30), offset);
        var metadata = Metadata(ticket, projection, offset);
        return new(projection.Kind, Attach(projection.Block, metadata), metadata);
    }

    private ReadBlockMetadata Metadata(ReadQueryTicket ticket, Projection projection, int offset)
    {
        var next = offset + projection.Count;
        var hasNext = next < projection.Total && projection.Count > 0;
        return new(ticket.QueryId, ticket.Tool, ticket.Arguments, ticket.Revision,
            projection.Stale ? "stale" : hasNext || projection.Recovery is not null ? "partial" : "complete", tokens.Issue(ticket),
            hasNext ? tokens.Issue(ticket with { Offset = next }) : null, projection.Total,
            projection.Recovery ?? (projection.Stale ? "refresh" : null));
    }

    private sealed record Projection(string Kind, object? Block, string? Revision = null,
        bool Stale = false, int? Total = null, int Count = 0, string? Recovery = null);

    private static Projection? Project(string tool, object? payload, Guid userId, int offset) => payload switch
    {
        HabitListCard card => new("habitList", card, card.SourceRevision, Total: card.TotalCount ?? card.Items.Count, Count: card.Items.Count),
        GoalListCard card => GoalProjection(tool, card, offset),
        MetricsCard metrics => new("metrics", metrics),
        HabitMetrics metrics => new("metrics", MetricsCardBuilder.BuildHabit(metrics), metrics.SourceRevision),
        PeriodAnalyticsResponse analytics => analytics.Comparison is { } comparison
            ? new("periodComparison", comparison, analytics.SourceRevision)
            : new("periodTotal", analytics.Total, analytics.SourceRevision),
        HabitHistoryRead history => new("recordList", new RecordListCard("habitHistory", history.TotalCount,
            history.Logs.Select(log => new RecordListItem(log.Id.ToString(),
                log.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), LogDate: log.Date, Value: log.Value)).ToList()),
            history.SourceRevision, Total: history.TotalCount, Count: history.Logs.Count),
        DailySummaryResponse summary => new("daySummary", summary.ReadCard, summary.SourceRevision),
        RetrospectiveResponse retrospective => new("periodInsight", PeriodInsightCardBuilder.Build(retrospective),
            retrospective.SourceRevision, retrospective.FromCache),
        HabitMonthCard month => new("habitMonth", month),
        CalendarOverviewPayload calendar => CalendarProjection(calendar, offset),
        GamificationOverviewPayload gamification => GamificationProjection(gamification),
        ProfileResponse profile => new("accountRows", AccountRowsCardBuilder.Profile(profile)),
        SubscriptionOverviewPayload subscription => new("accountRows", AccountRowsCardBuilder.Plan(subscription)),
        ReferralDashboardResponse referral => new("accountRows", AccountRowsCardBuilder.Referral(referral)),
        GetNotificationsResponse notifications => RecordProjection(RecordListCardBuilder.BuildNotificationPage(notifications, userId, offset), notifications.SourceRevision),
        IReadOnlyList<TagResponse> tags => RecordProjection(RecordListCardBuilder.BuildTags(tags, userId, offset)),
        IReadOnlyList<ChecklistTemplateResponse> templates => RecordProjection(RecordListCardBuilder.BuildTemplates(templates, userId, offset)),
        IReadOnlyList<ApiKeyResponse> keys => KeyProjection(keys, userId, offset),
        _ => null
    };

    private static Projection KeyProjection(IReadOnlyList<ApiKeyResponse> keys, Guid userId, int offset)
    {
        var nowAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime;
        var revision = ReadRevision.Hash(new
        {
            Keys = keys,
            States = keys.Select(k => new { k.Id, Expired = k.ExpiresAtUtc <= nowAtUtc })
        });
        return RecordProjection(RecordListCardBuilder.BuildKeys(keys, nowAtUtc, userId, offset), revision);
    }

    private static Projection? GamificationProjection(GamificationOverviewPayload payload) =>
        StatusCardBuilder.BuildStreak(payload) is { } streak
            ? new("streak", streak)
            : payload is { Profile: null, Achievements: null, Streak: null }
                ? null
                : new("gamification", new GamificationCard(payload));

    private static Projection RecordProjection(RecordListCard card, string? revision = null) =>
        new("recordList", card, revision, Total: card.TotalCount, Count: card.Items.Count);

    private static Projection GoalProjection(string tool, GoalListCard card, int offset)
    {
        var page = tool == "review_goals" ? card with { Items = card.Items.Skip(offset).Take(20).ToList() } : card;
        return new("goalList", page, card.SourceRevision ?? ReadRevision.Hash(card),
            Total: card.TotalCount ?? card.Items.Count, Count: page.Items.Count);
    }

    private static Projection CalendarProjection(CalendarOverviewPayload payload, int offset)
    {
        var events = payload.Events.OrderBy(e => e.StartUtc).ThenBy(e => e.StartDate, StringComparer.Ordinal)
            .ThenBy(e => e.Id, StringComparer.Ordinal).Skip(offset).ToList();
        return new("calendar", StatusCardBuilder.BuildCalendar(payload with { Events = events }),
            Total: payload.Events.Count, Count: Math.Min(10, events.Count), Recovery: payload.Recovery);
    }

    private static object Attach(object block, ReadBlockMetadata read) => block switch
    {
        HabitListCard card => card with { Read = read },
        GoalListCard card => card with { Read = read },
        MetricsCard card => card with { Read = read },
        PeriodInsightCard card => card with { Read = read },
        DaySummaryCard card => card with { Read = read },
        StreakCard card => card with { Read = read },
        GamificationCard card => card with { Read = read },
        HabitMonthCard card => card with { Read = read },
        CalendarCard card => card with { Read = read },
        RecordListCard card => card with { Read = read },
        AccountRowsCard card => card with { Read = read },
        PeriodTotalCard card => card with { Read = read },
        PeriodComparisonCard card => card with { Read = read },
        _ => block
    };
}
