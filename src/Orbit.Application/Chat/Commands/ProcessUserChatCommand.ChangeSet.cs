using Orbit.Domain.Common;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Commands;

public partial class ProcessUserChatCommandHandler
{
    private async Task CombinePendingChangesAsync(ProcessUserChatCommand request, ToolExecutionAccumulator results, CancellationToken ct)
    {
        if (results.PendingOperations.Count < 2) return;
        var members = results.PendingOperations.Select(pending => execution.PendingOperationStore.GetExecution(request.UserId, pending.Id))
            .Where(member => member is not null && AiToolAtomicity.Supports(ai.ToolRegistry.GetTool(member.OperationId), member.Arguments)).ToArray();
        if (members.Length < 2 || members.Any(member => member!.RequiresReproposal)) return;
        var arguments = AccountChangeSet.BuildArguments(members.Select(member => member!));
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
        var fingerprint = AgentOperationFingerprint.Compute(AccountChangeSet.OperationName, arguments.GetRawText());
        var pending = execution.PendingOperationStore.Create(request.UserId, capability, AccountChangeSet.OperationName,
            arguments.GetRawText(), "Review account changes", fingerprint, AgentExecutionSurface.Chat);
        if (!execution.PendingOperationStore.Revise(request.UserId, pending.Id, fingerprint, arguments.GetRawText(), fingerprint, preview.PreviewFingerprint!))
            throw new InvalidOperationException("The account change preview could not be bound.");
        var combinedIds = members.Select(member => member!.PendingOperationId).ToHashSet();
        results.PendingOperations.RemoveAll(pending => combinedIds.Contains(pending.Id));
        results.PendingOperations.Add(pending with
        {
            Items = preview.Items,
            Changes = preview.Changes,
            ChangeTargetCount = preview.ChangeTargetCount,
            PreviewFingerprint = preview.PreviewFingerprint
        });
    }
}
