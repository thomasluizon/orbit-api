using System.Text.Json;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public partial class AgentOperationExecutor
{
    private async Task<AgentConfirmationRequirement?> ChangeSetConfirmationAsync(AgentExecuteOperationRequest request,
        JsonElement arguments, AgentConfirmationRequirement? required, CancellationToken ct)
    {
        if (request.OperationId != AccountChangeSet.OperationId || AccountChangeSet.Read(arguments) is not { } members) return required;
        foreach (var member in members)
        {
            var stored = pendingOperationStore.GetExecution(request.UserId, member.PendingOperationId);
            if (stored is null) continue;
            var requirement = await stepUpAuthorizationBridge.GetRequiredConfirmationAsync(stored.CapabilityId, ct) ?? stored.ConfirmationRequirement;
            required = (AgentConfirmationRequirement)Math.Max((int)(required ?? AgentConfirmationRequirement.FreshConfirmation), (int)requirement);
        }
        return required;
    }

    private async Task<(IReadOnlyList<OperationExecutionContext>? Members, string? Error)> PrepareChangeSetAsync(
        OperationExecutionContext execution, CancellationToken ct)
    {
        var members = AccountChangeSet.Read(execution.Arguments);
        if (members is null) return (null, "invalid_change_set");
        var prepared = new List<OperationExecutionContext>();
        foreach (var member in members)
        {
            var stored = pendingOperationStore.GetExecution(execution.Request.UserId, member.PendingOperationId);
            if (stored is null || stored.RequiresReproposal || stored.Surface != execution.Request.Surface
                || stored.OperationId != member.OperationId
                || AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText())
                    != (member.SourceFingerprint ?? AgentOperationFingerprint.Compute(member.OperationId, member.Arguments.GetRawText())))
                return (null, "stale_preview");
            var operation = catalogService.GetOperation(member.OperationId);
            var capability = operation is null ? null : catalogService.GetCapability(operation.CapabilityId);
            if (operation is not { IsMutation: true, IsAgentExecutable: true } || capability is null
                || toolRegistry.GetTool(operation.Id) is not { } tool)
                return (null, "unsupported_by_policy");
            var ownership = await targetOwnershipService.GetDenialReasonAsync(operation.Id, execution.Request.UserId, member.Arguments, ct);
            if (ownership is not null) return (null, ownership);
            var current = await changePreviewer.PreviewAsync(execution.Request.UserId, member.OperationId, member.Arguments, ct);
            if (current.IsFailure || current.Value?.PreviewFingerprint != member.PreviewFingerprint
                || current.Value.Items is not { Count: > 0 } items)
                return (null, "stale_preview");
            var error = items.SelectMany(item => item.ValidationErrors ?? []).FirstOrDefault();
            if (error is not null) return (null, error.ErrorCode);
            if (tool is IArgumentCheckTool check)
            {
                var validation = await check.CheckArgumentsAsync(member.Arguments, execution.Request.UserId, ct);
                if (validation.IsFailure) return (null, validation.ErrorCode ?? "invalid_arguments");
            }
            var request = execution.Request with { OperationId = operation.Id, Arguments = member.Arguments, ConfirmationToken = null };
            var context = new OperationExecutionContext(request, operation, capability, member.Arguments,
                $"{operation.DisplayName} approved in account changes", AgentConfirmationRequirement.None);
            var access = EvaluatePolicy(context);
            if (access.Status != AgentPolicyDecisionStatus.Allowed) return (null, access.Reason ?? "denied");
            prepared.Add(context);
        }
        return (prepared, null);
    }

    private async Task<ToolResult> ExecuteChangeSetAsync(OperationExecutionContext execution,
        IReadOnlyList<OperationExecutionContext> members, CancellationToken ct)
    {
        var results = new List<AgentOperationResult>();
        foreach (var member in members)
        {
            var result = await ExecutePolicyAndToolAsync(member, toolRegistry.GetTool(member.Operation.Id), new OperationPolicyState(), ct);
            results.Add(result.Operation);
            if (result.Operation.Status != AgentOperationStatus.Succeeded)
                return new ToolResult(false, Error: result.Operation.PolicyReason ?? result.Operation.ErrorCode ?? "change_set_failed", Payload: results);
        }
        foreach (var member in AccountChangeSet.Read(execution.Arguments)!)
            if (!pendingOperationStore.Cancel(execution.Request.UserId, member.PendingOperationId,
                member.SourceFingerprint ?? AgentOperationFingerprint.Compute(member.OperationId, member.Arguments.GetRawText())))
                throw new InvalidOperationException("An approved account change lost its reservation.");
        return new ToolResult(true, Payload: results);
    }
}
