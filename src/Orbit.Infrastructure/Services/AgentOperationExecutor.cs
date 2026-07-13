using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public partial class AgentOperationExecutor(
    IAgentCatalogService catalogService,
    IAgentPolicyEvaluator policyEvaluator,
    IAgentAuditService auditService,
    IAgentTargetOwnershipService targetOwnershipService,
    AiToolRegistry toolRegistry,
    IUnitOfWork unitOfWork,
    ILogger<AgentOperationExecutor> logger) : IAgentOperationExecutor
{
    private const int MaxToolConcurrencyAttempts = 3;
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement.Clone();

    public async Task<AgentExecuteOperationResponse> ExecuteAsync(
        AgentExecuteOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        var operation = catalogService.GetOperation(request.OperationId);
        if (operation is null)
            return AgentOperationResponseFactory.UnknownOperation(request.OperationId);

        var capability = catalogService.GetCapability(operation.CapabilityId)
            ?? throw new InvalidOperationException($"Operation '{operation.Id}' is mapped to an unknown capability '{operation.CapabilityId}'.");

        var arguments = request.Arguments.ValueKind == JsonValueKind.Undefined
            ? EmptyArguments
            : request.Arguments;
        var execution = new OperationExecutionContext(
            request,
            operation,
            capability,
            arguments,
            $"{operation.DisplayName} requested via {request.Surface}");

        if (!operation.IsAgentExecutable)
            return await DenyDirectUserFlowAsync(execution, cancellationToken);

        var ownershipDenialReason = await targetOwnershipService.GetDenialReasonAsync(
            operation.Id,
            request.UserId,
            arguments,
            cancellationToken);

        if (ownershipDenialReason is not null)
            return await DenyOwnershipAsync(execution, ownershipDenialReason, cancellationToken);

        var policyDecision = EvaluatePolicy(execution);

        if (policyDecision.Status == AgentPolicyDecisionStatus.Denied)
            return await DenyByPolicyAsync(execution, policyDecision, cancellationToken);

        if (policyDecision.Status == AgentPolicyDecisionStatus.ConfirmationRequired)
            return await RequireConfirmationAsync(execution, policyDecision, cancellationToken);

        var tool = toolRegistry.GetTool(operation.Id);
        if (tool is null)
            return AgentOperationResponseFactory.MissingTool(
                execution.Operation.Id,
                execution.Capability.RiskClass,
                execution.Capability.ConfirmationRequirement,
                execution.Summary);

        return await ExecuteToolAsync(tool, execution, policyDecision, cancellationToken);
    }

    private async Task<AgentExecuteOperationResponse> DenyDirectUserFlowAsync(
        OperationExecutionContext execution,
        CancellationToken cancellationToken)
    {
        var summary = $"{execution.Operation.DisplayName} requires a direct client flow.";

        await TryAuditAsync(
            CreateAuditContext(
                execution.Request,
                execution.Capability,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Denied,
                summary,
                RedactArguments(execution.Arguments),
                error: "direct_user_flow_required"),
            cancellationToken);

        return DeniedResponse(execution, summary, "direct_user_flow_required", "direct_user_flow_required");
    }

    private async Task<AgentExecuteOperationResponse> DenyOwnershipAsync(
        OperationExecutionContext execution,
        string denialReason,
        CancellationToken cancellationToken)
    {
        await TryAuditAsync(
            CreateAuditContext(
                execution.Request,
                execution.Capability,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Denied,
                execution.Summary,
                RedactArguments(execution.Arguments),
                error: denialReason),
            cancellationToken);

        return DeniedResponse(execution, execution.Summary, denialReason, denialReason);
    }

    private AgentPolicyDecision EvaluatePolicy(OperationExecutionContext execution)
    {
        var grantedScopes = GetGrantedScopes(execution.Request);
        var operationFingerprint = AgentOperationFingerprint.Compute(
            execution.Operation.Id,
            execution.Arguments.GetRawText());

        return policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            execution.Capability.Id,
            execution.Request.UserId,
            execution.Request.Surface,
            execution.Request.AuthMethod,
            grantedScopes,
            execution.Operation.Id,
            execution.Summary,
            operationFingerprint,
            execution.Arguments.GetRawText(),
            execution.Request.ConfirmationToken,
            IsReadOnlyCredential: execution.Request.IsReadOnlyCredential));
    }

    private async Task<AgentExecuteOperationResponse> DenyByPolicyAsync(
        OperationExecutionContext execution,
        AgentPolicyDecision policyDecision,
        CancellationToken cancellationToken)
    {
        await TryAuditAsync(
            CreateAuditContext(
                execution.Request,
                execution.Capability,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Denied,
                execution.Summary,
                RedactArguments(execution.Arguments),
                error: policyDecision.Reason,
                shadowPolicyDecision: policyDecision.ShadowStatus,
                shadowReason: policyDecision.ShadowReason),
            cancellationToken);

        return DeniedResponse(execution, execution.Summary, policyDecision.Reason, policyDecision.Reason ?? "denied");
    }

    private async Task<AgentExecuteOperationResponse> RequireConfirmationAsync(
        OperationExecutionContext execution,
        AgentPolicyDecision policyDecision,
        CancellationToken cancellationToken)
    {
        await TryAuditAsync(
            CreateAuditContext(
                execution.Request,
                execution.Capability,
                AgentPolicyDecisionStatus.ConfirmationRequired,
                AgentOperationStatus.PendingConfirmation,
                execution.Summary,
                RedactArguments(execution.Arguments),
                error: policyDecision.Reason,
                shadowPolicyDecision: policyDecision.ShadowStatus,
                shadowReason: policyDecision.ShadowReason),
            cancellationToken);

        return AgentOperationResponseFactory.ConfirmationRequired(
            execution.Operation.Id,
            execution.Capability.RiskClass,
            execution.Capability.ConfirmationRequirement,
            execution.Summary,
            policyDecision.Reason,
            policyDecision.PendingOperation);
    }

    private async Task<AgentExecuteOperationResponse> ExecuteToolAsync(
        IAiTool tool,
        OperationExecutionContext execution,
        AgentPolicyDecision policyDecision,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ExecuteToolWithConcurrencyRetryAsync(tool, execution, cancellationToken);
            return await BuildToolOutcomeResponseAsync(execution, policyDecision, result, cancellationToken);
        }
        catch (Exception ex)
        {
            await TryAuditAsync(
                CreateAuditContext(
                    execution.Request,
                    execution.Capability,
                    AgentPolicyDecisionStatus.Allowed,
                    AgentOperationStatus.Failed,
                    execution.Summary,
                    RedactArguments(execution.Arguments),
                    error: ex.Message,
                    shadowPolicyDecision: policyDecision.ShadowStatus,
                    shadowReason: policyDecision.ShadowReason),
                cancellationToken);

            return AgentOperationResponseFactory.Failed(
                execution.Operation.Id,
                execution.Capability.RiskClass,
                execution.Capability.ConfirmationRequirement,
                execution.Summary);
        }
    }

    private async Task<ToolResult> ExecuteToolWithConcurrencyRetryAsync(
        IAiTool tool,
        OperationExecutionContext execution,
        CancellationToken cancellationToken)
    {
        if (tool is not IConcurrencyRetryableTool)
            return await tool.ExecuteAsync(execution.Arguments, execution.Request.UserId, cancellationToken);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await tool.ExecuteAsync(execution.Arguments, execution.Request.UserId, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxToolConcurrencyAttempts)
            {
                unitOfWork.ResetTracking();
            }
        }
    }

    private async Task<AgentExecuteOperationResponse> BuildToolOutcomeResponseAsync(
        OperationExecutionContext execution,
        AgentPolicyDecision policyDecision,
        ToolResult result,
        CancellationToken cancellationToken)
    {
        var isPayGateDenial = !result.Success && result.ErrorCode == Result.PayGateErrorCode;
        AgentOperationStatus outcomeStatus;
        if (result.Success)
            outcomeStatus = AgentOperationStatus.Succeeded;
        else
            outcomeStatus = isPayGateDenial ? AgentOperationStatus.Denied : AgentOperationStatus.Failed;

        await TryAuditAsync(
            CreateAuditContext(
                execution.Request,
                execution.Capability,
                AgentPolicyDecisionStatus.Allowed,
                outcomeStatus,
                execution.Summary,
                RedactArguments(execution.Arguments),
                result.EntityId,
                result.EntityName,
                result.Error,
                policyDecision.ShadowStatus,
                policyDecision.ShadowReason),
            cancellationToken);

        return AgentOperationResponseFactory.ToolOutcome(
            execution.Operation.Id,
            execution.Capability.RiskClass,
            execution.Capability.ConfirmationRequirement,
            execution.Summary,
            result,
            outcomeStatus,
            isPayGateDenial);
    }

    private static AgentExecuteOperationResponse DeniedResponse(
        OperationExecutionContext execution,
        string summary,
        string? policyReason,
        string denialReason)
    {
        return AgentOperationResponseFactory.Denied(
            execution.Operation.Id,
            execution.Capability.RiskClass,
            execution.Capability.ConfirmationRequirement,
            summary,
            policyReason,
            denialReason);
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
        catch (Exception ex)
        {
            LogAuditWriteFailed(logger, ex, context.Request.OperationId, context.Request.UserId, context.Request.CorrelationId);
        }
    }

    private static string? RedactArguments(JsonElement arguments) =>
        AgentAuditRedactor.Redact(arguments.GetRawText());

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Failed to record agent audit entry for operation {OperationId} (user {UserId}, correlation {CorrelationId})")]
    private static partial void LogAuditWriteFailed(ILogger logger, Exception ex, string operationId, Guid userId, string? correlationId);

    private sealed record OperationExecutionContext(
        AgentExecuteOperationRequest Request,
        AgentOperation Operation,
        AgentCapability Capability,
        JsonElement Arguments,
        string Summary);

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
