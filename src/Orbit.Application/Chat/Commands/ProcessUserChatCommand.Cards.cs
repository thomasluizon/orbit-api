using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Profile.Queries;
using Orbit.Application.Referrals.Queries;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<ResponseCards> BuildResponsePartsAsync(
        ParsedResponseDirectives parsed, bool isTruncated, string? language,
        ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, CancellationToken cancellationToken)
    {
        var parts = new List<ChatResponsePart>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockCount = 0;
        foreach (var segment in parsed.Segments)
        {
            if (segment.Text is { } text)
            {
                parts.Add(new ChatTextPart(text));
                continue;
            }

            if (segment.Directive is not { } directive || blockCount >= 3 || !seen.Add(directive))
                continue;

            var bound = ResolveQueryRead(directive, results);
            var kind = bound?.Kind ?? DirectivePartKind(directive);
            if (bound is not null)
            {
                if (!ReadEnabled(bound, request.ClientContext, context.EnabledFeatureFlags))
                {
                    if (ReadFallback(bound.Block, language, context.User?.TimeZone, context.UserToday) is { Length: > 0 } fallback) parts.Add(new ChatTextPart(fallback));
                    continue;
                }
                parts.Add(new ChatBlockPart(bound.Kind, ForClient(bound, request.ClientContext, context.EnabledFeatureFlags).Block));
                blockCount++;
                continue;
            }
            if (kind is null || request.ClientContext?.PartKinds?.Contains(kind, StringComparer.Ordinal) != true)
                continue;
            if (directive.StartsWith("[[orbit:read:", StringComparison.OrdinalIgnoreCase)) continue;
            var cards = await TryBuildResponseCardsAsync(directive, request, context, results, cancellationToken);
            object? block = kind switch
            {
                PartKinds.HabitList => cards.HabitList,
                PartKinds.GoalList => cards.GoalList,
                PartKinds.Metrics => cards.MetricsCard,
                PartKinds.PeriodInsight => cards.PeriodInsight,
                PartKinds.HabitRanking => cards.HabitRanking,
                PartKinds.DaySummary => cards.DaySummary,
                PartKinds.Streak => cards.Streak,
                PartKinds.Calendar => cards.Calendar,
                PartKinds.RecordList => cards.RecordLists is { Count: > 0 } lists ? lists[0] : null,
                PartKinds.AccountRows => cards.AccountRows,
                _ => null
            };
            if (block is null)
            {
                if (!string.IsNullOrWhiteSpace(cards.AiMessage)) parts.Add(new ChatTextPart(cards.AiMessage));
                continue;
            }

            parts.Add(new ChatBlockPart(kind, block));
            blockCount++;
        }

        if (isTruncated)
            parts.Add(new ChatTextPart(AppendTruncationNotice(null, language)));

        return new ResponseCards(string.Join("\n\n", parts.OfType<ChatTextPart>().Select(part => part.Text)),
            Parts: parts);
    }

    private static string? DirectivePartKind(string directive) => directive.ToLowerInvariant() switch
    {
        "[[orbit:habits:today]]" or "[[orbit:habits:remaining]]" or "[[orbit:habits:all]]" => PartKinds.HabitList,
        "[[orbit:goals]]" => PartKinds.GoalList,
        "[[orbit:metrics]]" => PartKinds.Metrics,
        "[[orbit:insight]]" => PartKinds.PeriodInsight,
        HabitRankingCardBuilder.Directive => PartKinds.HabitRanking,
        "[[orbit:day]]" => PartKinds.DaySummary,
        "[[orbit:streak]]" => PartKinds.Streak,
        "[[orbit:calendar]]" => PartKinds.Calendar,
        "[[orbit:records:notifications]]" or "[[orbit:records:tags]]"
            or "[[orbit:records:templates]]" or "[[orbit:records:keys]]" => PartKinds.RecordList,
        "[[orbit:account:profile]]" or "[[orbit:account:plan]]" or "[[orbit:account:referral]]" => PartKinds.AccountRows,
        _ => null
    };

    private async Task<ResponseCards> TryBuildResponseCardsAsync(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator executionResults, CancellationToken cancellationToken)
    {
        try
        {
            aiMessage = ReplaceUnavailableReadDirectives(aiMessage, executionResults, request.ClientContext, context.EnabledFeatureFlags, context.Language, context.User?.TimeZone, context.UserToday);
            var boundRead = ResolveQueryRead(aiMessage, executionResults);
            if (boundRead is not null && ReadEnabled(boundRead, request.ClientContext, context.EnabledFeatureFlags))
                return ResponseFromRead(StripAllDirectives(aiMessage), ForClient(boundRead, request.ClientContext, context.EnabledFeatureFlags));
            return RetainCompatibleCards(await BuildResponseCardsAsync(aiMessage, request, context, executionResults, cancellationToken), request.ClientContext, context.Language, context.User?.TimeZone);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResponseCardBuildThrew(logger, ex);
            return new ResponseCards(StripAllDirectives(aiMessage));
        }
    }

    private static string? StripAllDirectives(string? message) =>
        message is null ? null : Regex.Replace(message, @"\[\[orbit:(?:read:[A-Za-z0-9_-]+|[a-z:]+)\]\]", string.Empty,
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Trim();

    private async Task<ResponseCards> BuildResponseCardsAsync(
        string? aiMessage,
        ProcessUserChatCommand request,
        ChatContext context,
        ToolExecutionAccumulator executionResults,
        CancellationToken cancellationToken)
    {
        HabitListCard? habitList = null;
        if (HabitListCardBuilder.TryExtractScope(aiMessage, out var habitListScope, out var strippedMessage, out var remaining))
        {
            aiMessage = strippedMessage;
            if (request.ClientContext?.SupportsHabitListCard == true)
                habitList = await BuildHabitListFromQueryAsync(request, context, executionResults, habitListScope, remaining, cancellationToken);
        }

        GoalListCard? goalList = null;
        if (GoalListCardBuilder.TryExtractDirective(aiMessage, out var strippedGoalMessage))
        {
            aiMessage = strippedGoalMessage;
            if (request.ClientContext?.SupportsGoalListCard == true)
            {
                var includeProjections = !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AiGoalProjectionsDisabled);
                goalList = await BuildGoalReadAsync(request, context, executionResults, includeProjections, cancellationToken);
                if (!includeProjections && goalList is not null)
                    goalList = GoalListCardBuilder.WithoutProjections(goalList);
            }
        }

        MetricsCard? metricsCard = null;
        if (MetricsCardBuilder.TryExtractDirective(aiMessage, out var strippedMetricsMessage))
        {
            aiMessage = strippedMetricsMessage;
            if (request.ClientContext?.SupportsMetricsCard == true
                && !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AiPeriodBlocksDisabled))
            {
                var habitMetrics = executionResults.LastSuccessfulPayload<HabitMetrics>("get_habit_metrics");
                metricsCard = habitMetrics?.HabitId is not null
                    ? MetricsCardBuilder.BuildHabit(habitMetrics)
                    : await TryBuildMetricsCardAsync(request.UserId, context.UserToday, cancellationToken);
            }
        }

        PeriodInsightCard? periodInsight = null;
        if (aiMessage?.Contains(PeriodInsightCardBuilder.Directive, StringComparison.OrdinalIgnoreCase) == true
            && request.ClientContext?.SupportsPeriodInsightCard == true
            && !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AiPeriodBlocksDisabled))
        {
            periodInsight = PeriodInsightCardBuilder.Build(
                executionResults.LastSuccessfulPayload<RetrospectiveResponse>("get_retrospective"));
        }

        var habitRanking = BuildRequestedHabitRanking(aiMessage, request, context, executionResults);
        var (daySummary, streak, calendar) = BuildStatusCards(aiMessage, request, context, executionResults);
        var recordLists = BuildRecordLists(aiMessage, request, context, executionResults);
        var accountRows = BuildAccountRows(aiMessage, request, context, executionResults);

        aiMessage = StripAllDirectives(aiMessage);
        return CarryQueryReads(new ResponseCards(aiMessage, habitList, goalList, metricsCard, periodInsight,
            daySummary, streak, calendar, recordLists, accountRows, HabitRanking: habitRanking),
            executionResults, request.ClientContext, context.EnabledFeatureFlags);
    }

    private static AccountRowsCard? BuildAccountRows(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator results)
    {
        if (request.ClientContext?.SupportsAccountRowsCard != true
            || context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AiAccountRowsDisabled)
            || string.IsNullOrEmpty(aiMessage))
            return null;

        if (aiMessage.Contains(AccountRowsCardBuilder.ProfileDirective, StringComparison.OrdinalIgnoreCase))
            return results.LastSuccessfulPayload<ProfileResponse>("get_profile") is { } profile
                ? AccountRowsCardBuilder.Profile(profile) : null;

        if (aiMessage.Contains(AccountRowsCardBuilder.ReferralDirective, StringComparison.OrdinalIgnoreCase))
            return results.LastSuccessfulPayload<ReferralDashboardResponse>("get_referral_overview") is { } referral
                ? AccountRowsCardBuilder.Referral(referral) : null;

        if (!aiMessage.Contains(AccountRowsCardBuilder.PlanDirective, StringComparison.OrdinalIgnoreCase)
            || results.LastSuccessfulOperation("get_subscription_overview") is null
                && results.LastSuccessfulOperation("get_profile") is null)
            return null;

        if (results.LastSuccessfulPayload<ProfileResponse>("get_profile") is { } profilePayload)
            return AccountRowsCardBuilder.Plan(profilePayload);

        return results.LastSuccessfulPayload<SubscriptionOverviewPayload>("get_subscription_overview") is { } subscription
            ? AccountRowsCardBuilder.Plan(subscription) : null;
    }

    private static IReadOnlyList<RecordListCard>? BuildRecordLists(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results)
    {
        if (request.ClientContext?.SupportsRecordListCard != true
            || context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AiRecordListsDisabled)
            || string.IsNullOrEmpty(aiMessage))
            return null;

        var kinds = Regex.Matches(aiMessage, @"\[\[orbit:records:(notifications|tags|templates|keys)\]\]",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            .Cast<Match>()
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal);
        var cards = new List<RecordListCard>();
        foreach (var kind in kinds)
        {
            RecordListCard? card = kind switch
            {
                "notifications" => results.LastSuccessfulPayload<GetNotificationsResponse>("get_notifications") is { } notifications
                    ? RecordListCardBuilder.BuildNotifications(notifications, request.UserId) : null,
                "tags" => results.LastSuccessfulPayload<IReadOnlyList<TagResponse>>("list_tags") is { } tags
                    ? RecordListCardBuilder.BuildTags(tags, request.UserId) : null,
                "templates" => results.LastSuccessfulPayload<IReadOnlyList<ChecklistTemplateResponse>>("get_checklist_templates") is { } templates
                    ? RecordListCardBuilder.BuildTemplates(templates, request.UserId) : null,
                "keys" => results.LastSuccessfulPayload<IReadOnlyList<ApiKeyResponse>>("get_api_keys") is { } keys
                    ? RecordListCardBuilder.BuildKeys(keys, TimeProvider.System.GetUtcNow().UtcDateTime, request.UserId) : null,
                _ => null
            };
            if (card is not null)
                cards.Add(card);
        }

        return cards.Count == 0 ? null : cards;
    }

    private static (DaySummaryCard? Day, StreakCard? Streak, CalendarCard? Calendar) BuildStatusCards(
        string? aiMessage, ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator executionResults)
    {
        if (context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AiStatusBlocksDisabled))
            return (null, null, null);

        DaySummaryCard? day = null;
        if (request.ClientContext?.SupportsDaySummaryCard == true
            && aiMessage?.Contains(StatusCardBuilder.DayDirective, StringComparison.OrdinalIgnoreCase) == true
            && executionResults.LastSuccessfulPayload<DailySummaryResponse>("get_daily_summary") is not null)
        {
            day = AdaptDay(executionResults.LastSuccessfulPayload<DailySummaryResponse>("get_daily_summary")?.ReadCard, request.ClientContext?.SupportsHabitListDoneStatus);
        }

        var streak = request.ClientContext?.SupportsStreakCard == true
            && aiMessage?.Contains(StatusCardBuilder.StreakDirective, StringComparison.OrdinalIgnoreCase) == true
            ? StatusCardBuilder.BuildStreak(
                executionResults.LastSuccessfulPayload<GamificationOverviewPayload>("get_gamification_overview",
                    payload => payload.Profile is not null && payload.Streak is not null))
            : null;
        var calendar = request.ClientContext?.SupportsCalendarCard == true
            && aiMessage?.Contains(StatusCardBuilder.CalendarDirective, StringComparison.OrdinalIgnoreCase) == true
            ? StatusCardBuilder.BuildCalendar(
                executionResults.LastSuccessfulPayload<CalendarOverviewPayload>("get_calendar_overview"))
            : null;
        return (day, streak, calendar);
    }

    private async Task<MetricsCard?> TryBuildMetricsCardAsync(
        Guid userId,
        DateOnly userToday,
        CancellationToken cancellationToken)
    {
        try
        {
            var weekStartDay = await execution.UserDateService.GetUserWeekStartDayAsync(userId, cancellationToken);
            var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve("week", userToday, weekStartDay);
            var result = await execution.Mediator.Send(new GetPeriodAnalyticsQuery(userId, dateFrom, dateTo), cancellationToken);
            if (result.IsFailure) return null;
            var card = MetricsCardBuilder.BuildPeriod(result.Value.Total);
            var args = JsonSerializer.SerializeToElement(new
            {
                date_from = dateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date_to = dateTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                metric = "positiveLogRecords"
            });
            return readBlocks?.Capture("get_period_analytics", args, userId, card, revisionOverride: result.Value.SourceRevision)?.Block as MetricsCard ?? card;

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogMetricsCardBuildThrew(logger, ex);
            return null;
        }
    }
}
