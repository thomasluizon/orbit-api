using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private bool CanCompleteRevisionRound(AiResponse response, ProcessUserChatCommand request,
        ToolExecutionAccumulator results, IReadOnlyList<AiToolCall> calls,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes) =>
        TerminalReadDirective.IsComplete(response) && !results.IsPreviewDiscussion
        && calls.Count == response.ToolCalls!.Count && outcomes.Count == calls.Count
        && calls.All(call => call.Name == "revise_pending_changes"
            && outcomes.TryGetValue(call.Id, out var outcome)
            && outcome.ToolResult.Success && outcome.TraceStatus == AgentOperationStatus.PendingConfirmation
            && outcome.PendingOperation is { Items.Count: > 0 } pending
            && IsCurrentPendingOutcome(request.UserId, pending, results));
}
