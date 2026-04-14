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
            var argumentsToAudit = request.Arguments.ValueKind == JsonValueKind.Undefined
                ? EmptyArguments
                : request.Arguments;
            var redactedArguments = RedactArguments(argumentsToAudit);

            await TryAuditAsync(
                CreateAuditContext(
                    request,
                    capability,
                    AgentPolicyDecisionStatus.Denied,
                    AgentOperationStatus.Denied,
                    $"{operation.DisplayName} requires a direct client flow.",
                    redactedArguments,
                    error: "direct_user_flow_required"),
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
                CreateAuditContext(
                    request,
                    capability,
                    AgentPolicyDecisionStatus.Denied,
                    AgentOperationStatus.Denied,
                    summary,
                    RedactArguments(arguments),
                    error: ownershipDenialReason),
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

        var grantedScopes = GetGrantedScopes(request);

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
                CreateAuditContext(
                    request,
                    capability,
                    AgentPolicyDecisionStatus.Denied,
                    AgentOperationStatus.Denied,
                    summary,
                    RedactArguments(arguments),
                    error: policyDecision.Reason,
                    shadowPolicyDecision: policyDecision.ShadowStatus,
                    shadowReason: policyDecision.ShadowReason),
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
                CreateAuditContext(
                    request,
                    capability,
                    AgentPolicyDecisionStatus.ConfirmationRequired,
                    AgentOperationStatus.PendingConfirmation,
                    summary,
                    RedactArguments(arguments),
                    error: policyDecision.Reason,
                    shadowPolicyDecision: policyDecision.ShadowStatus,
                    shadowReason: policyDecision.ShadowReason),
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
                CreateAuditContext(
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
                    policyDecision.ShadowReason),
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
                CreateAuditContext(
                    request,
                    capability,
                    AgentPolicyDecisionStatus.Allowed,
                    AgentOperationStatus.Failed,
                    summary,
                    RedactArguments(arguments),
                    error: ex.Message,
                    shadowPolicyDecision: policyDecision.ShadowStatus,
                    shadowReason: policyDecision.ShadowReason),
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

    private IReadOnlyList<string> GetGrantedScopes(AgentExecuteOperationRequest request)
    {
        if (request.AuthMethod == AgentAuthMethod.ApiKey)
            return request.GrantedScopes ?? [];

        if (request.GrantedScopes is { Count: > 0 })
            return request.GrantedScopes;

        return catalogService.GetCapabilities()
            .Select(item => item.Scope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AuditContext CreateAuditContext(
        AgentExecuteOperationRequest request,
        AgentCapability capability,
        AgentPolicyDecisionStatus policyDecision,
        AgentOperationStatus outcomeStatus,
        string summary,
        string? redactedArguments,
        string? targetId = null,
        string? targetName = null,
        string? error = null,
        AgentPolicyDecisionStatus? shadowPolicyDecision = null,
        string? shadowReason = null)
    {
        return new AuditContext(
            request,
            capability,
            policyDecision,
            outcomeStatus,
            summary,
            redactedArguments,
            targetId,
            targetName,
            error,
            shadowPolicyDecision,
            shadowReason);
    }

    private async Task TryAuditAsync(
        AuditContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditService.RecordAsync(new AgentAuditEntry(
                context.Request.UserId,
                context.Capability.Id,
                context.Request.OperationId,
                context.Request.Surface,
                context.Request.AuthMethod,
                context.Capability.RiskClass,
                context.PolicyDecision,
                context.OutcomeStatus,
                context.Request.CorrelationId,
                context.Summary,
                context.TargetId,
                context.TargetName,
                context.RedactedArguments,
                context.Error,
                context.ShadowPolicyDecision,
                context.ShadowReason), cancellationToken);
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

    private sealed record AuditContext(
        AgentExecuteOperationRequest Request,
        AgentCapability Capability,
        AgentPolicyDecisionStatus PolicyDecision,
        AgentOperationStatus OutcomeStatus,
        string Summary,
        string? RedactedArguments,
        string? TargetId,
        string? TargetName,
        string? Error,
        AgentPolicyDecisionStatus? ShadowPolicyDecision,
        string? ShadowReason);
}
