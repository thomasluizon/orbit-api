using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static HabitRankingCard? BuildRequestedHabitRanking(string? aiMessage, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results) =>
        aiMessage?.Contains(HabitRankingCardBuilder.Directive, StringComparison.OrdinalIgnoreCase) == true
        && HabitRankingCardBuilder.IsSupported(request.ClientContext)
        && !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraPeriodBlocksDisabled)
            ? HabitRankingCardBuilder.Build(results.LastSuccessfulPayload<HabitRankingResponse>("get_habit_ranking")) : null;

    private static bool HasOnlySuccessfulRankingReads(ToolExecutionAccumulator results) =>
        results.OperationResults.Count > 0 && results.OperationResults.All(operation =>
            operation.OperationId == "get_habit_ranking" && operation.Status == AgentOperationStatus.Succeeded)
        && results.LastSuccessfulPayload<HabitRankingResponse>("get_habit_ranking") is not null;

    private ResponseCards EnsureHabitRankingResult(ResponseCards cards, ProcessUserChatCommand request,
        ChatContext context, ToolExecutionAccumulator results)
    {
        var ranking = results.LastSuccessfulPayload<HabitRankingResponse>("get_habit_ranking");
        if (ranking is null)
            return cards;
        var supported = HabitRankingCardBuilder.IsSupported(request.ClientContext)
            && !context.EnabledFeatureFlags.Contains(FeatureFlagKeys.AstraPeriodBlocksDisabled);
        var card = HabitRankingCardBuilder.Build(ranking);
        var failed = card is null;
        AnalyticsCapture.SafeCaptureUserEvent(execution.ProductAnalytics, logger, request.UserId,
            context.User?.Plan.ToString(), "habit_ranking_block", new Dictionary<string, object>
            {
                ["kind"] = HabitRankingCardBuilder.Kind,
                ["metric"] = HabitRankingDefinition.CompletionLogRecords,
                ["result_count"] = ranking.Rows.Count,
                ["complete"] = ranking.IsComplete,
                ["build_failure"] = failed
            });
        if (cards.Parts?.OfType<ChatBlockPart>().Any(part => part.Kind == HabitRankingCardBuilder.Kind) == true)
            return cards;
        if (supported && card is not null && (cards.Parts?.OfType<ChatBlockPart>().Count() ?? 0) < 3)
            return cards with { Parts = (cards.Parts ?? []).Append(new ChatBlockPart(HabitRankingCardBuilder.Kind, card)).ToArray() };
        var readable = failed
            ? ToolFailureMessage(context.Language)
            : HabitRankingCardBuilder.Readable(ranking, context.Language);
        return cards with
        {
            AiMessage = string.Join("\n\n", new[] { cards.AiMessage, readable }.Where(text => !string.IsNullOrWhiteSpace(text))),
            Parts = request.ClientContext?.SupportsParts == true
                ? (cards.Parts ?? []).Append(new ChatTextPart(readable)).ToArray() : null
        };
    }

    private ChatResponse CompleteRankingWithoutNarrative(ProcessUserChatCommand request, ChatContext context,
        ToolExecutionAccumulator results)
    {
        var cards = EnsureHabitRankingResult(new ResponseCards(null), request, context, results);
        return new ChatResponse(cards.AiMessage, [], results.OperationResults, CorrelationId: request.CorrelationId,
            Parts: cards.Parts);
    }
}
