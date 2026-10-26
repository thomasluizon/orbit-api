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
        if (SuggestionAnswer(request, executionResults) is { } suggestions)
        {
            if (request.StreamSink is not null) await request.StreamSink(ChatStreamEvent.Reset());
            return (suggestions, null, TurnFactSet.FromToolResults(executionResults.ToolResults));
        }
        return await ComposeRegularReplyCoreAsync(request, context, executionResults, toolLoopResult,
            aiResponse, groundingTurn, cancellationToken);
    }

    private static ResponseCards? SuggestionAnswer(ProcessUserChatCommand request, ToolExecutionAccumulator results)
    {
        if (results.PendingOperations.Count > 0 || results.ActionResults.Count > 0) return null;
        var card = results.OperationResults.LastOrDefault(operation => operation.Status == AgentOperationStatus.Succeeded
            && operation.Payload is HabitSuggestionsCard)?.Payload as HabitSuggestionsCard;
        if (card is null) return null;
        var intro = HabitSuggestionsPresentation.Introduction(card.Language);
        return new(intro, Parts: [new ChatTextPart(intro), new ChatBlockPart("habitSuggestions", card)])
        { ServerComposedText = true };
    }
}
