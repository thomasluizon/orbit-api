using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public class AgentOperationExecutor(
    IAgentCatalogService catalogService,
    IAgentPolicyEvaluator policyEvaluator,
    IAgentAuditService auditService,
    IAgentTargetOwnershipService targetOwnershipService,
    AiToolRegistry toolRegistry) : IAgentOperationExecutor
{
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement.Clone();

    public async Task<AgentExecuteOperationResponse> ExecuteAsync(
        AgentExecuteOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        var operation = catalogService.GetOperation(request.OperationId);
        if (operation is null)
        {
            return new AgentExecuteOperationResponse(
                new AgentOperationResult(
                    request.OperationId,
                    request.OperationId,
                    AgentRiskClass.Low,
                    AgentConfirmationRequirement.None,
                    AgentOperationStatus.UnsupportedByPolicy,
                    PolicyReason: "unsupported_by_policy"),
                PolicyDenial: new AgentPolicyDenial(
                    request.OperationId,
                    request.OperationId,
                    AgentRiskClass.Low,
                    AgentConfirmationRequirement.None,
                    "unsupported_by_policy"));
        }

        var capability = catalogService.GetCapability(operation.CapabilityId)
            ?? throw new InvalidOperationException($"Operation '{operation.Id}' is mapped to an unknown capability '{operation.CapabilityId}'.");

        if (!operation.IsAgentExecutable)
        {
            var redactedArguments = request.Arguments.ValueKind == JsonValueKind.Undefined
                ? RedactArguments(EmptyArguments)
                : RedactArguments(request.Arguments);

            await TryAuditAsync(
                request,
                capability,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Denied,
                $"{operation.DisplayName} requires a direct client flow.",
                redactedArguments,
                null,
                null,
                "direct_user_flow_required",
                null,
                null,
                cancellationToken);

            return new AgentExecuteOperationResponse(
                new AgentOperationResult(
                    operation.Id,
                    operation.Id,
                    capability.RiskClass,
                    capability.ConfirmationRequirement,
                    AgentOperationStatus.Denied,
                    Summary: $"{operation.DisplayName} requires a direct client flow.",
                    PolicyReason: "direct_user_flow_required"),
                PolicyDenial: new AgentPolicyDenial(
                    operation.Id,
                    operation.Id,
                    capability.RiskClass,
                    capability.ConfirmationRequirement,
                    "direct_user_flow_required"));
        }

        var arguments = request.Arguments.ValueKind == JsonValueKind.Undefined
            ? EmptyArguments
            : request.Arguments;
        var summary = $"{operation.DisplayName} requested via {request.Surface}";

        var ownershipDenialReason = await targetOwnershipService.GetDenialReasonAsync(
            operation.Id,
            request.UserId,
            arguments,
            cancellationToken);

        if (ownershipDenialReason is not null)
        {
            await TryAuditAsync(
                request,
                capability,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Denied,
                summary,
                RedactArguments(arguments),
                null,
                null,
                ownershipDenialReason,
                null,
                null,
                cancellationToken);

            return new AgentExecuteOperationResponse(
                new AgentOperationResult(
                    operation.Id,
                    operation.Id,
                    capability.RiskClass,
                    capability.ConfirmationRequirement,
                    AgentOperationStatus.Denied,
                    Summary: summary,
                    PolicyReason: ownershipDenialReason),
                PolicyDenial: new AgentPolicyDenial(
                    operation.Id,
                    operation.Id,
                    capability.RiskClass,
                    capability.ConfirmationRequirement,
                    ownershipDenialReason));
        }

        var grantedScopes = request.AuthMethod == AgentAuthMethod.ApiKey
            ? request.GrantedScopes ?? []
            : (request.GrantedScopes is { Count: > 0 }
                ? request.GrantedScopes
                : catalogService.GetCapabilities()
                    .Select(item => item.Scope)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var operationFingerprint = $"{operation.Id}:{arguments.GetRawText()}";
        var policyDecision = policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            capability.Id,
            request.UserId,
            request.Surface,
            request.AuthMethod,
            grantedScopes,
            operation.Id,
            summary,
            operationFingerprint,
            arguments.GetRawText(),
            request.ConfirmationToken,
            IsReadOnlyCredential: request.IsReadOnlyCredential));

        if (policyDecision.Status == AgentPolicyDecisionStatus.Denied)
        {
            await TryAuditAsync(
                request,
                capability,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Denied,
                summary,
                RedactArguments(arguments),
                null,
                null,
                policyDecision.Reason,
                policyDecision.ShadowStatus,
                policyDecision.ShadowReason,
                cancellationToken);

            return new AgentExecuteOperationResponse(
                new AgentOperationResult(
                    operation.Id,
                    operation.Id,
                    capability.RiskClass,
                    capability.ConfirmationRequirement,
                    AgentOperationStatus.Denied,
                    Summary: summary,
                    PolicyReason: policyDecision.Reason),
                PolicyDenial: new AgentPolicyDenial(
                    operation.Id,
                    operation.Id,
                    capability.RiskClass,
                    capability.ConfirmationRequirement,
                    policyDecision.Reason ?? "denied"));
        }

        if (policyDecision.Status == AgentPolicyDecisionStatus.ConfirmationRequired)
        {
            await TryAuditAsync(
                request,
                capability,
                AgentPolicyDecisionStatus.ConfirmationRequired,
                AgentOperationStatus.PendingConfirmation,
                summary,
                RedactArguments(arguments),
                null,
                null,
                policyDecision.Reason,
                policyDecision.ShadowStatus,
                policyDecision.ShadowReason,
                cancellationToken);

            return new AgentExecuteOperationResponse(
                new AgentOperationResult(
                    operation.Id,
                    operation.Id,
                    capability.RiskClass,
                    capability.ConfirmationRequirement,
                    AgentOperationStatus.PendingConfirmation,
                    Summary: summary,
                    PolicyReason: policyDecision.Reason,
                    PendingOperationId: policyDecision.PendingOperation?.Id),
                PendingOperation: policyDecision.PendingOperation);
        }

        var tool = toolRegistry.GetTool(operation.Id);
        if (tool is null)
        {
            return new AgentExecuteOperationResponse(
                new AgentOperationResult(
                    operation.Id,
                    operation.Id,
                    capability.RiskClass,
                    capability.ConfirmationRequirement,
                    AgentOperationStatus.UnsupportedByPolicy,
                    Summary: summary,
                    PolicyReason: "missing_tool_executor"),
                PolicyDenial: new AgentPolicyDenial(
                    operation.Id,
                    operation.Id,
                    capability.RiskClass,
                    capability.ConfirmationRequirement,
                    "missing_tool_executor"));
        }

        try
        {
            var result = await tool.ExecuteAsync(arguments, request.UserId, cancellationToken);
            var outcomeStatus = result.Success ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed;

            await TryAuditAsync(
                request,
                capability,
                AgentPolicyDecisionStatus.Allowed,
                outcomeStatus,
                summary,
                RedactArguments(arguments),
                result.EntityId,
                result.EntityName,
                result.Error,
                policyDecision.ShadowStatus,
                policyDecision.ShadowReason,
                cancellationToken);

            return new AgentExecuteOperationResponse(new AgentOperationResult(
                operation.Id,
                operation.Id,
                capability.RiskClass,
                capability.ConfirmationRequirement,
                outcomeStatus,
                Summary: summary,
                TargetId: result.EntityId,
                TargetName: result.EntityName,
                PolicyReason: result.Success ? null : result.Error,
                Payload: result.Payload));
        }
        catch (Exception ex)
        {
            await TryAuditAsync(
                request,
                capability,
                AgentPolicyDecisionStatus.Allowed,
                AgentOperationStatus.Failed,
                summary,
                RedactArguments(arguments),
                null,
                null,
                ex.Message,
                policyDecision.ShadowStatus,
                policyDecision.ShadowReason,
                cancellationToken);

            return new AgentExecuteOperationResponse(new AgentOperationResult(
                operation.Id,
                operation.Id,
                capability.RiskClass,
                capability.ConfirmationRequirement,
                AgentOperationStatus.Failed,
                Summary: summary,
                PolicyReason: "unexpected_error"));
        }
    }

    private async Task TryAuditAsync(
        AgentExecuteOperationRequest request,
        AgentCapability capability,
        AgentPolicyDecisionStatus policyDecision,
        AgentOperationStatus outcomeStatus,
        string summary,
        string? redactedArguments,
        string? targetId,
        string? targetName,
        string? error,
        AgentPolicyDecisionStatus? shadowPolicyDecision,
        string? shadowReason,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditService.RecordAsync(new AgentAuditEntry(
                request.UserId,
                capability.Id,
                request.OperationId,
                request.Surface,
                request.AuthMethod,
                capability.RiskClass,
                policyDecision,
                outcomeStatus,
                request.CorrelationId,
                summary,
                targetId,
                targetName,
                redactedArguments,
                error,
                shadowPolicyDecision,
                shadowReason), cancellationToken);
        }
        catch
        {
            // Audit failures must not block the operation path.
        }
    }

    private static string? RedactArguments(JsonElement arguments)
    {
        var raw = arguments.GetRawText();
        return raw.Length <= 1000 ? raw : raw[..1000];
    }
}
