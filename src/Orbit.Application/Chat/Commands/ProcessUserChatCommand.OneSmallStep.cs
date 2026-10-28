using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private static bool CanCompleteStartingStepRound(AiResponse response, IReadOnlyList<AiToolCall> calls,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes) => calls.Count > 0
        && calls.Count == response.ToolCalls?.Count && outcomes.Count == calls.Count
        && calls.All(call => call.Name == "suggest_breakdown" && outcomes.TryGetValue(call.Id, out var outcome)
            && outcome.OperationResult is { Status: AgentOperationStatus.Succeeded, Payload: StartingStep });

    private static ResponseCards? StartingStepAnswer(ToolExecutionAccumulator results)
    {
        if (results.PendingOperations.Count > 0 || results.ActionResults.Any(action => action.Status != ActionStatus.Suggestion))
            return null;
        var suggestion = results.LastSuccessfulPayload<StartingStep>("suggest_breakdown");
        return suggestion is null ? null : new(null,
            Parts: [new ChatBlockPart("oneSmallStep", OneSmallStepCard.Build(suggestion))])
        { ServerComposedText = true };
    }
}
