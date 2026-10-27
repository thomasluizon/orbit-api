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
        if (request.OperationId != AccountChangeSet.OperationName || AccountChangeSet.Read(arguments) is not { } members) return required;
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
            var result = await PrepareChangeSetMemberAsync(execution, member, ct);
            if (result.Error is not null) return (null, result.Error);
            prepared.Add(result.Member!);
        }
        return (prepared, null);
    }

    private async Task<(OperationExecutionContext? Member, string? Error)> PrepareChangeSetMemberAsync(
        OperationExecutionContext execution, AccountChangeSet.Member member, CancellationToken ct)
    {
        if (!IsCurrentMember(execution, member)) return (null, "stale_preview");
        var operation = catalogService.GetOperation(member.OperationId);
        var capability = operation is null ? null : catalogService.GetCapability(operation.CapabilityId);
        if (operation is not { IsMutation: true, IsAgentExecutable: true } || capability is null
            || !await AiToolAtomicity.SupportsAsync(toolRegistry.GetTool(operation.Id), execution.Request.UserId, member.Arguments, ct))
            return (null, "unsupported_by_policy");
        var ownership = await targetOwnershipService.GetDenialReasonAsync(operation.Id, execution.Request.UserId, member.Arguments, ct);
        if (ownership is not null) return (null, ownership);
        var error = await ValidateChangeSetMemberAsync(execution.Request.UserId, member, ct, execution.Request.ClientContext);
        if (error is not null) return (null, error);
        var request = execution.Request with { OperationId = operation.Id, Arguments = member.Arguments, ConfirmationToken = null };
        var context = new OperationExecutionContext(request, operation, capability, member.Arguments,
            $"{operation.DisplayName} approved in account changes", AgentConfirmationRequirement.None);
        var access = EvaluatePolicy(context);
        return access.Status == AgentPolicyDecisionStatus.Allowed ? (context, null) : (null, access.Reason ?? "denied");
    }

    private bool IsCurrentMember(OperationExecutionContext execution, AccountChangeSet.Member member)
    {
        var stored = pendingOperationStore.GetExecution(execution.Request.UserId, member.PendingOperationId);
        return stored is not null && !stored.RequiresReproposal && stored.Surface == execution.Request.Surface
            && stored.OperationId == member.OperationId
            && AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText())
                == (member.SourceFingerprint ?? AgentOperationFingerprint.Compute(member.OperationId, member.Arguments.GetRawText()));
    }

    private async Task<string?> ValidateChangeSetMemberAsync(Guid userId, AccountChangeSet.Member member, CancellationToken ct, AgentClientContext? clientContext)
    {
        var current = await changePreviewer.PreviewAsync(userId, member.OperationId, member.Arguments, ct, clientContext);
        if (current.IsFailure || current.Value?.PreviewFingerprint != member.PreviewFingerprint
            || current.Value.Items is not { Count: > 0 } items)
            return "stale_preview";
        var error = items.SelectMany(item => item.ValidationErrors ?? []).FirstOrDefault();
        if (error is not null) return error.ErrorCode;
        if (toolRegistry.GetTool(member.OperationId) is not IArgumentCheckTool check) return null;
        var validation = await check.CheckArgumentsAsync(member.Arguments, userId, ct);
        return validation.IsFailure ? validation.ErrorCode ?? "invalid_arguments" : null;
    }

    private async Task<ToolResult> ExecuteChangeSetAsync(OperationExecutionContext execution,
        IReadOnlyList<OperationExecutionContext> members, CancellationToken ct)
    {
        var results = new List<AgentOperationResult>();
        foreach (var member in members)
        {
            var result = await ExecutePolicyAndToolAsync(member, toolRegistry.GetTool(member.Operation.Id), new OperationPolicyState(), ct);
            results.Add(result.Operation);
            if (result.Operation.Status != AgentOperationStatus.Succeeded || IsPartialMutation(result.Operation.Payload))
                return new ToolResult(false, Error: result.Operation.PolicyReason ?? result.Operation.ErrorCode ?? "change_set_failed", Payload: results);
        }
        var lostReservation = AccountChangeSet.Reservations(execution.Arguments).Any(member =>
            !pendingOperationStore.Cancel(execution.Request.UserId, member.PendingOperationId, member.Fingerprint));
        if (lostReservation)
            throw new InvalidOperationException("An approved account change lost its reservation.");
        return new ToolResult(true, Payload: results);
    }

    private static bool IsPartialMutation(object? result)
    {
        var payload = JsonSerializer.SerializeToElement(result);
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("partial", out var partial) && partial.ValueKind == JsonValueKind.True;
    }
}
