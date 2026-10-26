using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed record PendingAccountChangeSet(PendingAgentOperation PendingOperation, IReadOnlySet<Guid> MemberIds);

public sealed class AccountChangeSetService(IPendingAgentOperationStore store,
    IPendingOperationChangePreviewer previewer, IAgentCatalogService catalog, AiToolRegistry registry)
{
    public async Task<PendingAccountChangeSet?> CombineAsync(Guid userId, IReadOnlyList<PendingAgentOperation> operations,
        CancellationToken ct, AgentClientContext? clientContext = null, bool requireAllMembers = false)
    {
        if (operations.Count < 2) return null;
        var members = operations.Select(pending => store.GetExecution(userId, pending.Id))
            .OfType<PendingAgentOperationExecution>()
            .Where(member => AiToolAtomicity.Supports(registry.GetTool(member.OperationId), member.Arguments)).ToArray();
        if (members.Length < 2 || members.Any(member => member.RequiresReproposal)) return null;
        if (requireAllMembers && members.Length != operations.Count) return null;
        var arguments = AccountChangeSet.BuildArguments(members);
        var preview = await AccountChangeSet.PreviewAsync(userId, arguments, previewer, ct, clientContext);
        if (preview is null) return null;
        var capability = catalog.GetCapability(AccountChangeSet.CapabilityId)
            ?? throw new InvalidOperationException("The account changes capability is missing.");
        capability = capability with
        {
            RiskClass = operations.Max(pending => pending.RiskClass),
            ConfirmationRequirement = operations.Max(pending => pending.ConfirmationRequirement)
        };
        var fingerprint = AgentOperationFingerprint.Compute(AccountChangeSet.OperationName, arguments.GetRawText());
        var pending = store.Create(userId, capability, AccountChangeSet.OperationName,
            arguments.GetRawText(), "Review account changes", fingerprint, AgentExecutionSurface.Chat);
        try
        {
            if (!store.Revise(userId, pending.Id, fingerprint, arguments.GetRawText(), fingerprint, preview.PreviewFingerprint!))
                throw new InvalidOperationException("The account change preview could not be bound.");
        }
        catch
        {
            store.Cancel(userId, pending.Id, fingerprint);
            throw;
        }
        return new(pending with
        {
            Items = preview.Items,
            Changes = preview.Changes,
            ChangeTargetCount = preview.ChangeTargetCount,
            PreviewFingerprint = preview.PreviewFingerprint
        }, members.Select(member => member.PendingOperationId).ToHashSet());
    }
}
