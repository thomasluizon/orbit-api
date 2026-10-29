using Orbit.Domain.Models;
using Orbit.Application.Chat.Grounding;
using Orbit.Application.Chat.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task<(ResponseCards Cards, IReadOnlyList<string>? FollowUps, TurnFactSet Facts)> ComposeRegularReplyAsync(
        ProcessUserChatCommand request, ChatContext context, ToolExecutionAccumulator executionResults,
        ToolLoopResult toolLoopResult, AiResponse aiResponse, GroundingTurn groundingTurn, CancellationToken cancellationToken)
    {
        if ((CombinedCreationPreviewAnswer(executionResults, request) ?? StartingStepAnswer(executionResults, request, context)
            ?? SuggestionAnswer(executionResults, request, context)) is { } suggestions)
        {
            if (request.StreamSink is not null) await request.StreamSink(ChatStreamEvent.Reset());
            return (suggestions, null, TurnFactSet.FromToolResults(executionResults.ToolResults));
        }
        return await ComposeRegularReplyCoreAsync(request, context, executionResults, toolLoopResult,
            aiResponse, groundingTurn, cancellationToken);
    }

    private static ResponseCards? SuggestionAnswer(ToolExecutionAccumulator results,
        ProcessUserChatCommand request, ChatContext context)
    {
        if (results.PendingOperations.Count > 0 || results.ActionResults.Count > 0) return null;
        var card = results.OperationResults.LastOrDefault(operation => operation.Status == AgentOperationStatus.Succeeded
            && operation.Payload is HabitSuggestionsCard)?.Payload as HabitSuggestionsCard;
        if (card is null) return null;
        var response = ChatResponseNegotiation.ForClient(new ChatResponse(null, [],
            Parts: [new ChatBlockPart("habitSuggestions", card)]), request.ClientContext,
            context.EnabledFeatureFlags, context.Language, context.User?.TimeZone);
        return new(response.AiMessage, Parts: response.Parts)
        { ServerComposedText = true };
    }
}
