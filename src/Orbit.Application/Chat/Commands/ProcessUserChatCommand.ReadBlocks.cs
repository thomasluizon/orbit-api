using Microsoft.EntityFrameworkCore;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<HabitListCard?> BuildHabitListFromQueryAsync(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, string scope, bool remaining, CancellationToken ct)
    {
        var reads = results.OperationResults.Where(operation => operation.OperationId == "query_habits").ToList();
        if (reads.Count == 0)
        {
            if (readBlocks is not null)
            {
                var arguments = System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    date = scope == "today" ? context.UserToday.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
                    include_overdue = scope == "today",
                    is_completed = remaining ? (bool?)false : null
                });
                var response = await ExecuteImplicitReadAsync(request, "query_habits", arguments, ct);
                return response.Operation.ReadBlock is { } read
                    ? ForClient(read, request.ClientContext, context.EnabledFeatureFlags).Block as HabitListCard : null;
            }
            var habits = await data.HabitRepository.FindAsync(habit => habit.UserId == request.UserId,
                query => query.Include(habit => habit.Tags), ct);
            var facts = await HabitTodaySnapshot.ReadAsync(habits, context.UserToday,
                context.User?.WeekStartDay ?? 1, execution.ScheduleLogReader, ct);
            var visible = habits.Where(habit => !habit.IsCompleted || scope == HabitListCardBuilder.ScopeToday
                && (habit.Schedule is HabitSchedule.OneTime or HabitSchedule.General) && facts.DoneTodayIds.Contains(habit.Id)).ToList();
            return HabitListCardBuilder.Build(visible,
                context.UserToday, scope, facts,
                request.ClientContext?.SupportsHabitListDoneStatus == true, remaining);
        }
        if (reads.Count != 1 || reads[0].Status != AgentOperationStatus.Succeeded
            || reads[0].Payload is not HabitListCard queried)
            return null;
        return request.ClientContext?.SupportsHabitListDoneStatus == true
            ? queried
            : queried with
            {
                Items = queried.Items.Select(item => item.Status == HabitListCardBuilder.StatusDone
                    ? item with { Status = HabitListCardBuilder.StatusNone } : item).ToList()
            };
    }
    private async Task<GoalListCard?> BuildGoalReadAsync(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results, bool includeProjections, CancellationToken ct)
    {
        var reads = results.OperationResults.Where(r => r.OperationId is "query_goals" or "review_goals").ToList();
        if (reads.Count > 0)
            return reads.Count == 1 && reads[0].Status == AgentOperationStatus.Succeeded ? reads[0].Payload as GoalListCard : null;
        if (readBlocks is null)
        {
            var freshValues = await execution.GoalProgressReadSyncer.ComputeFreshValuesAsync(request.UserId, context.UserToday, ct);
            var goals = await data.GoalRepository.FindAsync(
                goal => goal.UserId == request.UserId && goal.Status == GoalStatus.Active,
                query => query.Include(goal => goal.Habits), ct);
            foreach (var goal in goals)
                if (freshValues.TryGetValue(goal.Id, out var fresh))
                    GoalProgressSyncService.ApplyReadValue(goal, fresh);
            return GoalListCardBuilder.Build(goals, context.UserToday, context.User?.WeekStartDay ?? 1, includeProjections);
        }
        var response = await ExecuteImplicitReadAsync(request, "query_goals",
            System.Text.Json.JsonSerializer.SerializeToElement(new { status = "Active", as_of = context.UserToday.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }), ct);
        return response.Operation.ReadBlock?.Block as GoalListCard;
    }

    private Task<AgentExecuteOperationResponse> ExecuteImplicitReadAsync(ProcessUserChatCommand request, string tool,
        System.Text.Json.JsonElement arguments, CancellationToken ct) => execution.OperationExecutor.ExecuteAsync(new(
            request.UserId, tool, arguments, AgentExecutionSurface.Chat, request.AuthMethod, request.GrantedScopes,
            request.IsReadOnlyCredential), ct);

}

public partial class ProcessUserChatCommandHandler
{
    private void CaptureReadMetadata(Guid userId, ResponseCards cards)
    {
        IEnumerable<object> blocks = cards.Parts is { } parts
            ? parts.OfType<ChatBlockPart>().Select(part => part.Block)
            : new object?[] { cards.HabitList, cards.GoalList, cards.MetricsCard, cards.PeriodInsight,
                cards.DaySummary, cards.Streak, cards.Calendar, cards.AccountRows }.OfType<object>()
                .Concat(cards.RecordLists ?? []);
        foreach (var block in blocks)
        {
            var (read, kind, metric) = ReadBlockTelemetry.Describe(block);
            if (read is not null)
                ReadBlockTelemetry.Capture(execution.ProductAnalytics, logger, userId, "chat_read_block_emitted", read, kind, metric);
        }
    }

    internal static QueryReadBlock ForClient(QueryReadBlock read, AgentClientContext? client, IReadOnlyList<string> flags) => read.Block switch
    {
        DaySummaryCard card when client?.SupportsHabitListDoneStatus != true => read with { Block = AdaptDay(card, client?.SupportsHabitListDoneStatus) ?? card },
        HabitListCard card when client?.SupportsHabitListDoneStatus != true => read with
        {
            Block = card with
            { Items = card.Items.Select(item => item.Status == "done" ? item with { Status = "none" } : item).ToList() }
        },
        GoalListCard card when flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraGoalProjectionsDisabled) =>
            read with { Block = GoalListCardBuilder.WithoutProjections(card) },
        _ => read
    };

    private static DaySummaryCard? AdaptDay(DaySummaryCard? card, bool? supportsDone) => card is null || supportsDone == true
        ? card : card with { Rows = card.Rows?.Select(row => row.Status == "done" ? row with { Status = "none" } : row).ToList() };

    private static ResponseCards CarryQueryReads(ResponseCards cards, ToolExecutionAccumulator results,
        AgentClientContext? client, IReadOnlyList<string> flags) => cards with
        {
            HabitList = BoundCard(cards.HabitList, results, "habitList", client, flags),
            GoalList = BoundCard(cards.GoalList, results, "goalList", client, flags),
            MetricsCard = BoundCard(cards.MetricsCard, results, "metrics", client, flags),
            PeriodInsight = BoundCard(cards.PeriodInsight, results, "periodInsight", client, flags),
            DaySummary = BoundCard(cards.DaySummary, results, "daySummary", client, flags),
            Streak = BoundCard(cards.Streak, results, "streak", client, flags),
            Calendar = BoundCard(cards.Calendar, results, "calendar", client, flags),
            RecordLists = cards.RecordLists?.Select(card => ReadCandidates("[[orbit:records:" + card.Kind + "]]", results)
                .SingleOrDefault()?.Block as RecordListCard ?? card).ToList(),
            AccountRows = cards.AccountRows is { } account ? ReadCandidates("[[orbit:account:" + account.Kind + "]]", results)
            .SingleOrDefault()?.Block as AccountRowsCard ?? account : null
        };

    private static T? BoundCard<T>(T? original, ToolExecutionAccumulator results, string kind,
        AgentClientContext? client, IReadOnlyList<string> flags) where T : class
    {
        if (original is null) return null;
        var reads = results.QueryReads.Values.Where(read => read.Kind == kind).ToList();
        return reads.Count == 1 ? ForClient(reads[0], client, flags).Block as T ?? original : original;
    }

    private static QueryReadBlock? ResolveQueryRead(string? directive, ToolExecutionAccumulator results)
    {
        if (directive is null) return null;
        var matches = System.Text.RegularExpressions.Regex.Matches(directive,
            @"\[\[orbit:(?:read:[A-Za-z0-9_-]+|[a-z:]+)\]\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        if (matches.Count == 1) directive = matches[0].Value;
        if (directive.StartsWith("[[orbit:read:", StringComparison.OrdinalIgnoreCase) && directive.EndsWith("]]", StringComparison.Ordinal))
        {
            var callId = directive[13..^2];
            return results.QueryReads.GetValueOrDefault(callId);
        }
        var kind = DirectivePartKind(directive.Trim());
        if (kind is null) return null;
        var reads = results.QueryReads.Values.Where(read => read.Kind == kind).ToList();
        if (kind == "recordList")
            reads = reads.Where(read => read.Block is RecordListCard records
                && directive.Contains(":" + records.Kind + "]]", StringComparison.OrdinalIgnoreCase)).ToList();
        if (kind == "accountRows")
            reads = reads.Where(read => read.Block is AccountRowsCard account
                && directive.Contains(":" + account.Kind + "]]", StringComparison.OrdinalIgnoreCase)).ToList();
        return reads.Count == 1 ? reads[0] : null;
    }

    private static string? ReplaceUnavailableReadDirectives(string? message, ToolExecutionAccumulator results,
        AgentClientContext? client, IReadOnlyList<string> flags, string? language, string? timeZone, DateOnly userToday)
    {
        if (message is null) return null;
        return System.Text.RegularExpressions.Regex.Replace(message, @"\[\[orbit:(?:read:[A-Za-z0-9_-]+|[a-z:]+)\]\]", match =>
        {
            var candidates = ResolveQueryRead(match.Value, results) is { } bound
                ? new List<QueryReadBlock> { bound } : ReadCandidates(match.Value, results);
            return candidates.Count > 1 || candidates.Count == 1 && !ReadEnabled(candidates[0], client, flags)
                ? string.Join("\n\n", candidates.Select(read => ReadFallback(read.Block, language, timeZone, userToday))) : match.Value;
        }, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    internal static bool ReadSubtypeEnabled(object block, AgentClientContext? client, bool blockFirst = false) => block switch
    {
        PeriodTotalCard { Metric: "completedHabits" } => client?.SupportsCompletedHabitsMetric == true,
        PeriodComparisonCard { Metric: "completedHabits" } => client?.SupportsCompletedHabitsMetric == true,
        HabitListCard card when client?.SupportsSkippedHabitRows != true
            && card.Items.Any(item => HabitReadMessages.IsSkipped(card, item)) => false,
        HabitListCard card when (!blockFirst || client?.SupportsParts != true) && card.Read is { } read
            && HabitReadMessages.IsMissingMetricsLookup(new("habitList", card, read)) => false,
        HabitListCard { Scope: "date" } => client?.SupportsDatedHabitListCard == true,
        RecordListCard { Kind: "habitHistory" } => client?.SupportsHabitHistoryCard == true,
        _ => true
    };

    private static bool ReadEnabled(QueryReadBlock read, AgentClientContext? client, IReadOnlyList<string> flags) =>
        ReadKindEnabled(read.Kind, client, flags) && ReadSubtypeEnabled(read.Block, client,
            flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraBlockFirst));

    internal static string ReadFallback(object block, string? language, string? timeZone = null, DateOnly? userToday = null) =>
        new ReadFallbackText(language ?? (block as HabitMonthCard)?.Language, timeZone, userToday).Format(block);

    private static ResponseCards RetainCompatibleCards(ResponseCards cards, AgentClientContext? client, string? language, string? timeZone)
    {
        if (cards.HabitList is { } habit && !ReadSubtypeEnabled(habit, client))
            cards = cards with
            {
                HabitList = null,
                AiMessage = string.Join("\n\n", new[] { cards.AiMessage, ReadFallback(habit, language, timeZone) }
                .Where(text => !string.IsNullOrWhiteSpace(text)))
            };
        return cards;
    }

    private static bool ShouldComposeReadFallbacks(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results) => results.AskBlock is null && results.OperationResults.Count > 0
        && results.OperationResults.All(operation => operation.Status == AgentOperationStatus.Succeeded && operation.ReadBlock is not null)
        && results.QueryReads.Values.Any(read => !ReadEnabled(read, request.ClientContext, context.EnabledFeatureFlags));

    private static ResponseCards ComposeUnavailableReads(ResponseCards cards, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results)
    {
        if (!ShouldComposeReadFallbacks(request, context, results)) return cards;
        var reads = SelectCompositionReads(results.QueryReads.Values.ToList(), results)
            .Where(read => request.ClientContext?.SupportsParts == true || !ReadEnabled(read, request.ClientContext, context.EnabledFeatureFlags))
            .DistinctBy(read => read.Read.QueryId);
        var parts = reads.Select(read => request.ClientContext?.SupportsParts == true
                && ReadEnabled(read, request.ClientContext, context.EnabledFeatureFlags)
            ? (ChatResponsePart)new ChatBlockPart(read.Kind, ForClient(read, request.ClientContext, context.EnabledFeatureFlags).Block)
            : new ChatTextPart(ReadFallback(read.Block, context.Language, context.User?.TimeZone, context.UserToday))).ToArray();
        return cards with
        {
            AiMessage = JoinPartText(parts),
            Parts = request.ClientContext?.SupportsParts == true ? parts : null,
            ServerComposedText = true
        };
    }

    private static bool HasOnlySuccessfulFocusedReads(ToolExecutionAccumulator results) =>
        results.QueryReads.Count > 0 && results.OperationResults.Count > 0
        && results.OperationResults.All(operation => operation.Status == AgentOperationStatus.Succeeded
            && operation.OperationId is "get_period_analytics" or "get_habit_metrics" or "get_habit_month");

    private ResponseCards EnsureFocusedReadResults(ResponseCards cards, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results)
    {
        foreach (var read in results.QueryReads.Values.Where(read => read.Read.QueryFamily is "get_period_analytics" or "get_habit_metrics" or "get_habit_month"))
        {
            var present = cards.Parts?.OfType<ChatBlockPart>().Any(part => ReadBlockTelemetry.Describe(part.Block).Read?.QueryId == read.Read.QueryId) == true
                || cards.MetricsCard?.Read?.QueryId == read.Read.QueryId;
            if (present) continue;
            if (request.ClientContext?.SupportsParts == true && ReadEnabled(read, request.ClientContext, context.EnabledFeatureFlags)
                && (cards.Parts?.OfType<ChatBlockPart>().Count() ?? 0) < 3)
                cards = cards with
                {
                    Parts = (cards.Parts ?? []).Append(new ChatBlockPart(read.Kind,
                    ForClient(read, request.ClientContext, context.EnabledFeatureFlags).Block)).ToArray()
                };
            else if (ReadFallback(read.Block, context.Language, context.User?.TimeZone, context.UserToday) is { Length: > 0 } text && cards.AiMessage?.Contains(text, StringComparison.Ordinal) != true)
                cards = cards with
                {
                    AiMessage = string.Join("\n\n", new[] { cards.AiMessage, text }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    Parts = request.ClientContext?.SupportsParts == true
                        ? (cards.Parts ?? []).Append(new ChatTextPart(text)).ToArray() : null
                };
        }
        return cards;
    }

    private ChatResponse CompleteFocusedReadsWithoutNarrative(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results)
    {
        var cards = ComposeHabitMonth(EnsureFocusedReadResults(new ResponseCards(null), request, context, results), request, context, results);
        CaptureReadMetadata(request.UserId, cards);
        return new ChatResponse(cards.AiMessage, [], results.OperationResults, CorrelationId: request.CorrelationId,
            Parts: cards.Parts);
    }

    private static bool HasOnlySupportedPeriodReads(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results) => !HasPendingWritePreview(results) && !results.HasBudgetRecovery
        && results.OperationResults.Count > 0 && results.OperationResults.All(operation =>
            operation.OperationId == "get_period_analytics" && operation.Status == AgentOperationStatus.Succeeded
            && operation.ReadBlock is { Kind: "periodTotal" or "periodComparison" } read
            && ReadEnabled(read, request.ClientContext, context.EnabledFeatureFlags));

    private ResponseCards ComposePeriodAnalytics(ResponseCards cards, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results)
    {
        if (!HasOnlySupportedPeriodReads(request, context, results)) return cards;
        cards = EnsureFocusedReadResults(cards, request, context, results);
        var blocks = cards.Parts?.OfType<ChatBlockPart>().ToArray();
        if (blocks is not { Length: > 0 } || blocks.Any(block => block.Kind is not ("periodTotal" or "periodComparison"))
            || results.QueryReads.Values.Any(read => !ReadEnabled(read, request.ClientContext, context.EnabledFeatureFlags)))
            return cards;
        return cards with { AiMessage = null, Parts = blocks, ServerComposedText = true };
    }

    private static List<QueryReadBlock> ReadCandidates(string directive, ToolExecutionAccumulator results)
    {
        var kind = DirectivePartKind(directive);
        var reads = results.QueryReads.Values.Where(read => read.Kind == kind);
        return reads.Where(read => read.Block switch
        {
            RecordListCard card => directive.Contains(":" + card.Kind + "]]", StringComparison.OrdinalIgnoreCase),
            AccountRowsCard card => directive.Contains(":" + card.Kind + "]]", StringComparison.OrdinalIgnoreCase),
            _ => true
        }).ToList();
    }

    internal static bool ReadKindEnabled(string kind, AgentClientContext? client, IReadOnlyList<string> flags)
    {
        if (client?.SupportsParts == true && client.PartKinds?.Contains(kind, StringComparer.Ordinal) != true) return false;
        return kind switch
        {
            "habitSuggestions" => SupportsSuggestionBlock(client),
            "habitMonth" => client?.SupportsParts == true
                && client.PartKinds?.Contains(kind, StringComparer.Ordinal) == true
                && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraStatusBlocksDisabled),
            "periodTotal" or "periodComparison" => client?.SupportsParts == true
                && client.PartKinds?.Contains(kind, StringComparer.Ordinal) == true
                && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraPeriodBlocksDisabled),
            "habitList" => client?.SupportsHabitListCard == true,
            "goalList" => client?.SupportsGoalListCard == true,
            "metrics" => client?.SupportsMetricsCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraPeriodBlocksDisabled),
            "periodInsight" => client?.SupportsPeriodInsightCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraPeriodBlocksDisabled),
            "daySummary" or "streak" or "calendar" or "gamification" => StatusCardBuilder.IsSupported(kind, client)
                && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraStatusBlocksDisabled),
            "recordList" => client?.SupportsRecordListCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraRecordListsDisabled),
            "accountRows" => client?.SupportsAccountRowsCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraAccountRowsDisabled),
            _ => false
        };
    }

    private static bool SupportsSuggestionBlock(AgentClientContext? client) => client?.SupportsParts == true
        && client.PartKinds?.Contains("habitSuggestions", StringComparer.Ordinal) == true;

    private static ResponseCards ResponseFromRead(string? message, QueryReadBlock read) => read.Block switch
    {
        HabitListCard card => new(message, HabitList: card),
        GoalListCard card => new(message, GoalList: card),
        MetricsCard card => new(message, MetricsCard: card),
        PeriodInsightCard card => new(message, PeriodInsight: card),
        DaySummaryCard card => new(message, DaySummary: card),
        StreakCard card => new(message, Streak: card),
        CalendarCard card => new(message, Calendar: card),
        RecordListCard card => new(message, RecordLists: [card]),
        AccountRowsCard card => new(message, AccountRows: card),
        _ => new(message)
    };
}
