using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private bool CanCompleteVerificationRound(ProcessUserChatCommand request, IReadOnlyList<AiToolCall> calls,
        IReadOnlyDictionary<string, ToolCallOutcome> outcomes) =>
        request.BlockAnswer is null && calls.Count > 0 && calls.Count == outcomes.Count
        && calls.All(call => ai.ToolRegistry.GetTool(call.Name)?.IsReadOnly == true
            && outcomes.TryGetValue(call.Id, out var outcome)
            && outcome.TraceStatus == AgentOperationStatus.PendingConfirmation
            && outcome.PendingOperation is { ConfirmationRequirement: AgentConfirmationRequirement.StepUp });
}
