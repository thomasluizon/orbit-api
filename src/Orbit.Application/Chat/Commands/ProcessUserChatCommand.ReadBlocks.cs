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
            return HabitListCardBuilder.Build(scope == HabitListCardBuilder.ScopeToday ? context.TodayHabits : context.ActiveHabits,
                context.UserToday, scope, context.TodayFacts,
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
            return GoalListCardBuilder.Build(context.ActiveGoals, context.UserToday, context.User?.WeekStartDay ?? 1, includeProjections);
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

    private static QueryReadBlock ForClient(QueryReadBlock read, AgentClientContext? client, IReadOnlyList<string> flags) => read.Block switch
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

    private static bool HasAmbiguousRead(string? message, ToolExecutionAccumulator results)
    {
        if (message is null) return false;
        var directives = System.Text.RegularExpressions.Regex.Matches(message, @"\[\[orbit:[a-z:]+\]\]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return directives.Any(match => ReadCandidates(match.Value, results).Count > 1);
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
            "periodTotal" or "periodComparison" => client?.SupportsParts == true
                && client.PartKinds?.Contains(kind, StringComparer.Ordinal) == true
                && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraPeriodBlocksDisabled),
            "habitList" => client?.SupportsHabitListCard == true,
            "goalList" => client?.SupportsGoalListCard == true,
            "metrics" => client?.SupportsMetricsCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraPeriodBlocksDisabled),
            "periodInsight" => client?.SupportsPeriodInsightCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraPeriodBlocksDisabled),
            "daySummary" => client?.SupportsDaySummaryCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraStatusBlocksDisabled),
            "streak" => client?.SupportsStreakCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraStatusBlocksDisabled),
            "calendar" => client?.SupportsCalendarCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraStatusBlocksDisabled),
            "recordList" => client?.SupportsRecordListCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraRecordListsDisabled),
            "accountRows" => client?.SupportsAccountRowsCard == true && !flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraAccountRowsDisabled),
            _ => false
        };
    }

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
