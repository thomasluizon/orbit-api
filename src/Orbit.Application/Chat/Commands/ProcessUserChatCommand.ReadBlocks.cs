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
    private static QueryReadBlock ForClient(QueryReadBlock read, AgentClientContext? client, IReadOnlyList<string> flags) => read.Block switch
    {
        HabitListCard card when client?.SupportsHabitListDoneStatus != true => read with
        {
            Block = card with
            { Items = card.Items.Select(item => item.Status == "done" ? item with { Status = "none" } : item).ToList() }
        },
        GoalListCard card when flags.Contains(Orbit.Application.Common.FeatureFlagKeys.AstraGoalProjectionsDisabled) =>
            read with { Block = GoalListCardBuilder.WithoutProjections(card) },
        _ => read
    };

    private static ResponseCards CarryQueryReads(ResponseCards cards, ToolExecutionAccumulator results) => cards with
    {
        HabitList = BoundCard(cards.HabitList, results, "habitList"),
        GoalList = BoundCard(cards.GoalList, results, "goalList"),
        MetricsCard = BoundCard(cards.MetricsCard, results, "metrics"),
        PeriodInsight = BoundCard(cards.PeriodInsight, results, "periodInsight"),
        DaySummary = BoundCard(cards.DaySummary, results, "daySummary"),
        Streak = BoundCard(cards.Streak, results, "streak"),
        Calendar = BoundCard(cards.Calendar, results, "calendar")
    };

    private static T? BoundCard<T>(T? original, ToolExecutionAccumulator results, string kind) where T : class =>
        original is null ? null : SingleRead<T>(results, kind) ?? original;

    private static T? SingleRead<T>(ToolExecutionAccumulator results, string kind) where T : class
    {
        var reads = results.QueryReads.Values.Where(read => read.Kind == kind).ToList();
        return reads.Count == 1 ? reads[0].Block as T : null;
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

    private static bool HasAmbiguousRead(string? directive, ToolExecutionAccumulator results)
    {
        if (directive is null) return false;
        var kind = DirectivePartKind(directive.Trim());
        return kind is not null && results.QueryReads.Values.Count(read => read.Kind == kind) > 1;
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
