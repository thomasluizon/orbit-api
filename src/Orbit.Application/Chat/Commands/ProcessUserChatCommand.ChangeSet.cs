using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task CombinePendingChangesAsync(ProcessUserChatCommand request, ToolExecutionAccumulator results, CancellationToken ct)
    {
        if (results.PendingOperations.Count < 2) return;
        var members = results.PendingOperations.Select(pending => execution.PendingOperationStore.GetExecution(request.UserId, pending.Id)).ToArray();
        if (members.Any(member => member is null || member.RequiresReproposal)) return;
        var arguments = AccountChangeSet.Arguments(members.Select(member => member!));
        var previewer = execution.PendingOperationPreviewer ?? new PendingOperationChangePreviewer(
            data.HabitRepository, data.GoalRepository, data.TagRepository, execution.UserDateService, ai.ToolRegistry);
        var preview = await AccountChangeSet.PreviewAsync(request.UserId, arguments, previewer, ct);
        if (preview is null) return;
        var capability = ai.CatalogService.GetCapability(AccountChangeSet.CapabilityId)
            ?? throw new InvalidOperationException("The account changes capability is missing.");
        capability = capability with
        {
            RiskClass = results.PendingOperations.Max(pending => pending.RiskClass),
            ConfirmationRequirement = results.PendingOperations.Max(pending => pending.ConfirmationRequirement)
        };
        var fingerprint = AgentOperationFingerprint.Compute(AccountChangeSet.OperationId, arguments.GetRawText());
        var pending = execution.PendingOperationStore.Create(request.UserId, capability, AccountChangeSet.OperationId,
            arguments.GetRawText(), "Review account changes", fingerprint, AgentExecutionSurface.Chat);
        if (!execution.PendingOperationStore.Revise(request.UserId, pending.Id, fingerprint, arguments.GetRawText(), fingerprint, preview.PreviewFingerprint!))
            throw new InvalidOperationException("The account change preview could not be bound.");
        results.PendingOperations.Clear();
        results.PendingOperations.Add(pending with
        {
            Items = preview.Items,
            Changes = preview.Changes,
            ChangeTargetCount = preview.ChangeTargetCount,
            PreviewFingerprint = preview.PreviewFingerprint
        });
    }
}
