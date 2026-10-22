using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orbit.Application.Chat;
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
    IAgentStepUpAuthorizationBridge stepUpAuthorizationBridge,
    AiToolRegistry toolRegistry,
    IPendingOperationChangePreviewer changePreviewer,
    IPendingAgentOperationStore pendingOperationStore,
    IUnitOfWork unitOfWork,
    ILogger<AgentOperationExecutor> logger) : IAgentOperationExecutor, Orbit.Application.Chat.Commands.IAgentMutationRunner
{
    private const int MaxToolConcurrencyAttempts = 3;
    private const string UnknownOperationCapabilityId = "unknown";
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement.Clone();

    public async Task<AgentExecuteOperationResponse> ExecuteAsync(
        AgentExecuteOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        var arguments = request.Arguments.ValueKind == JsonValueKind.Undefined
            ? EmptyArguments
            : request.Arguments;

        var operation = catalogService.GetOperation(request.OperationId);
        if (operation is null)
            return await DenyUnknownOperationAsync(request, arguments, cancellationToken);

        var declaredCapability = catalogService.GetCapability(operation.CapabilityId)
            ?? throw new InvalidOperationException($"Operation '{operation.Id}' is mapped to an unknown capability '{operation.CapabilityId}'.");

        var escalatedRequirement = await stepUpAuthorizationBridge.GetRequiredConfirmationAsync(
            declaredCapability.Id,
            cancellationToken);
        var tool = toolRegistry.GetTool(operation.Id);
        if (AgentChatWriteHold.Applies(
                request.Surface,
                operation,
                escalatedRequirement ?? declaredCapability.ConfirmationRequirement)
            && !AsksClarificationFirst(tool, arguments))
        {
            escalatedRequirement = AgentConfirmationRequirement.FreshConfirmation;
        }

        var capability = escalatedRequirement is { } required
            ? declaredCapability with { ConfirmationRequirement = required }
            : declaredCapability;
        var execution = new OperationExecutionContext(
            request,
            operation,
            capability,
            arguments,
            $"{operation.DisplayName} requested via {request.Surface}",
            escalatedRequirement);

        if (!operation.IsAgentExecutable)
            return await DenyDirectUserFlowAsync(execution, cancellationToken);

        var ownershipDenialReason = await targetOwnershipService.GetDenialReasonAsync(
            operation.Id,
            request.UserId,
            arguments,
            cancellationToken);

        if (ownershipDenialReason is not null)
            return await DenyOwnershipAsync(execution, ownershipDenialReason, cancellationToken);

        if (operation.IsMutation && request.ConfirmationToken is { } token)
        {
            var confirmed = pendingOperationStore.GetConfirmedExecution(request.UserId, capability.Id,
                AgentOperationFingerprint.Compute(operation.Id, arguments.GetRawText()), token);
            if (confirmed?.RequiresReproposal == true)
                return await DenyByPolicyAsync(execution, new AgentPolicyDecision(
                    AgentPolicyDecisionStatus.Denied, capability, "pending_operation_schema_changed"), cancellationToken);
            if (confirmed is not null && request.Surface == AgentExecutionSurface.Chat)
            {
                var current = await changePreviewer.TryPreviewAsync(request.UserId, operation.Id,
                    arguments, logger, cancellationToken);
                if (string.IsNullOrWhiteSpace(confirmed.PreviewFingerprint) || current.IsFailure
                    || current.Value?.Items is not { Count: > 0 }
                    || current.Value.PreviewFingerprint != confirmed.PreviewFingerprint)
                    return await DenyByPolicyAsync(execution, new AgentPolicyDecision(
                        AgentPolicyDecisionStatus.Denied, capability, "stale_preview"), cancellationToken);
            }
        }
        return await ExecuteWithPolicyAsync(execution, cancellationToken);
    }

    private static bool AsksClarificationFirst(IAiTool? tool, JsonElement arguments) =>
        tool is IClarificationPrecheckTool precheck && precheck.NeedsClarification(arguments);

    private async Task<AgentExecuteOperationResponse> DenyUnknownOperationAsync(
        AgentExecuteOperationRequest request,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        await TryAuditAsync(
            new AuditContext(
                request,
                UnknownOperationCapabilityId,
                AgentRiskClass.Low,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.UnsupportedByPolicy,
                $"Unknown operation '{request.OperationId}' requested via {request.Surface}",
                RedactArguments(arguments),
                Error: "unsupported_by_policy"),
            cancellationToken);

        return AgentOperationResponseFactory.UnknownOperation(request.OperationId);
    }

    private async Task<AgentExecuteOperationResponse> DenyDirectUserFlowAsync(
        OperationExecutionContext execution,
        CancellationToken cancellationToken)
    {
        var summary = $"{execution.Operation.DisplayName} requires a direct client flow.";

        await TryAuditAsync(
            new AuditContext(
                execution.Request,
                execution.Capability.Id,
                execution.Capability.RiskClass,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Denied,
                summary,
                RedactArguments(execution.Arguments),
                Error: "direct_user_flow_required"),
            cancellationToken);

        return DeniedResponse(execution, summary, "direct_user_flow_required", "direct_user_flow_required");
    }

    private async Task<AgentExecuteOperationResponse> DenyOwnershipAsync(
        OperationExecutionContext execution,
        string denialReason,
        CancellationToken cancellationToken)
    {
        await TryAuditAsync(
            new AuditContext(
                execution.Request,
                execution.Capability.Id,
                execution.Capability.RiskClass,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Denied,
                execution.Summary,
                RedactArguments(execution.Arguments),
                Error: denialReason),
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
            IsReadOnlyCredential: execution.Request.IsReadOnlyCredential,
            ConfirmationRequirementOverride: execution.EscalatedRequirement,
            PreviewOnly: execution.Request.PreviewOnly));
    }

    private async Task<AgentExecuteOperationResponse> DenyByPolicyAsync(
        OperationExecutionContext execution,
        AgentPolicyDecision policyDecision,
        CancellationToken cancellationToken)
    {
        await TryAuditAsync(
            new AuditContext(
                execution.Request,
                execution.Capability.Id,
                execution.Capability.RiskClass,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Denied,
                execution.Summary,
                RedactArguments(execution.Arguments),
                Error: policyDecision.Reason,
                ShadowPolicyDecision: policyDecision.ShadowStatus,
                ShadowReason: policyDecision.ShadowReason),
            cancellationToken);

        return DeniedResponse(execution, execution.Summary, policyDecision.Reason, policyDecision.Reason ?? "denied");
    }

    private async Task<AgentExecuteOperationResponse> RequireConfirmationAsync(
        OperationExecutionContext execution,
        AgentPolicyDecision policyDecision,
        CancellationToken cancellationToken)
    {
        await TryAuditAsync(
            new AuditContext(
                execution.Request,
                execution.Capability.Id,
                execution.Capability.RiskClass,
                AgentPolicyDecisionStatus.ConfirmationRequired,
                AgentOperationStatus.PendingConfirmation,
                execution.Summary,
                RedactArguments(execution.Arguments),
                Error: policyDecision.Reason,
                ShadowPolicyDecision: policyDecision.ShadowStatus,
                ShadowReason: policyDecision.ShadowReason),
            cancellationToken);

        var pending = policyDecision.PendingOperation;
        if (pending is not null && execution.Request.Surface == AgentExecutionSurface.Chat
            && (execution.Operation.IsMutation || execution.Request.IncludeChangePreview))
        {
            var attemptedPreview = await PrepareConfirmationPreviewAsync(execution, pending, cancellationToken);
            if (attemptedPreview.IsFailure)
            {
                pendingOperationStore.Cancel(execution.Request.UserId, pending.Id,
                    AgentOperationFingerprint.Compute(execution.Operation.Id, execution.Arguments.GetRawText()));
                return await BuildToolOutcomeResponseAsync(execution, policyDecision,
                    new ToolResult(false, Error: attemptedPreview.ErrorCode ?? attemptedPreview.Error,
                        ErrorCode: attemptedPreview.ErrorCode), cancellationToken);
            }
            var preview = attemptedPreview.Value;

            if (preview is null || preview.ChangeTargetCount is not > 0 || preview.Items is not { Count: > 0 }
                || string.IsNullOrWhiteSpace(preview.PreviewFingerprint))
            {
                pendingOperationStore.Cancel(execution.Request.UserId, pending.Id,
                    AgentOperationFingerprint.Compute(execution.Operation.Id, execution.Arguments.GetRawText()));
                return await BuildToolOutcomeResponseAsync(execution, policyDecision,
                    new ToolResult(false, Error: "preview_unavailable"), cancellationToken);
            }

            var expectedFingerprint = AgentOperationFingerprint.Compute(execution.Operation.Id,
                execution.Arguments.GetRawText());
            var argumentsJson = (preview.ExecutionArguments ?? execution.Arguments).GetRawText();
            var operationFingerprint = AgentOperationFingerprint.Compute(execution.Operation.Id, argumentsJson);
            if (!pendingOperationStore.Revise(execution.Request.UserId, pending.Id, expectedFingerprint,
                argumentsJson, operationFingerprint, preview.PreviewFingerprint))
                throw new InvalidOperationException("The pending operation preview binding could not be persisted.");

            pending = pending with
            {
                Changes = preview.Changes,
                ChangeTargetCount = preview.ChangeTargetCount,
                Items = preview.Items,
                PreviewFingerprint = preview.PreviewFingerprint
            };
        }

        return AgentOperationResponseFactory.ConfirmationRequired(
            execution.Operation.Id,
            execution.Capability.RiskClass,
            execution.Capability.ConfirmationRequirement,
            execution.Summary,
            policyDecision.Reason,
            pending);
    }

    private async Task<Result<PendingOperationChangePreview?>> PrepareConfirmationPreviewAsync(
        OperationExecutionContext execution, PendingAgentOperation pending, CancellationToken cancellationToken)
    {
        try
        {
            return await changePreviewer.TryPreviewAsync(execution.Request.UserId, execution.Operation.Id,
                execution.Arguments, logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            pendingOperationStore.Cancel(execution.Request.UserId, pending.Id,
                AgentOperationFingerprint.Compute(execution.Operation.Id, execution.Arguments.GetRawText()));
            throw;
        }
    }

    private async Task<AgentExecuteOperationResponse> ExecuteWithPolicyAsync(
        OperationExecutionContext execution,
        CancellationToken cancellationToken)
    {
        var tool = toolRegistry.GetTool(execution.Operation.Id);
        var policyState = new OperationPolicyState();

        try
        {
            if (tool is not ITransactionalAiTool)
                return await ExecutePolicyAndToolAsync(execution, tool, policyState, cancellationToken);
            return unitOfWork.HasActiveTransaction
                ? await unitOfWork.ExecuteInSavepointAsync(
                    ct => ExecutePolicyAndToolAsync(execution, tool, policyState, ct), cancellationToken)
                : await unitOfWork.ExecuteInTransactionAsync(
                    ct => ExecutePolicyAndToolAsync(execution, tool, policyState, ct), cancellationToken);
        }
        catch (ToolOutcomeRollbackException ex)
        {
            return await BuildToolOutcomeResponseAsync(execution, ex.Decision, ex.Result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
            && policyState.Decision?.Status == AgentPolicyDecisionStatus.ConfirmationRequired)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await FailOperationAsync(execution, ex, policyState.Decision, cancellationToken);
        }
    }

    private async Task<AgentExecuteOperationResponse> ExecutePolicyAndToolAsync(
        OperationExecutionContext execution,
        IAiTool? tool,
        OperationPolicyState policyState,
        CancellationToken cancellationToken)
    {
        policyState.Decision = null;
        PreparedToolIntent? preparedIntent = null;
        if (execution.Request.Surface == AgentExecutionSurface.Chat && execution.Operation.IsMutation
            && execution.Request.ConfirmationToken is null
            && tool is IPreparedIntentTool preparer)
        {
            preparedIntent = await preparer.PrepareIntentAsync(execution.Arguments, execution.Request.UserId, cancellationToken);
            if (preparedIntent is not null)
            {
                var check = preparedIntent.ArgumentValidation;
                if (check.IsFailure)
                {
                    var rejection = await BuildToolOutcomeResponseAsync(execution,
                        new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, execution.Capability),
                        ToolResult.FromFailure(check), cancellationToken);
                    return new RejectedBeforeDispatchResponse(rejection, execution.Request, preparedIntent);
                }
                execution = execution with { Arguments = preparedIntent.ExecutionArguments };
            }
        }
        var response = await ExecutePreparedPolicyAndToolAsync(execution, tool, policyState, preparedIntent is not null, cancellationToken);
        return preparedIntent is null ? response : new PreparedToolOperationResponse(response, execution.Request, preparedIntent);
    }

    private async Task<AgentExecuteOperationResponse> ExecutePreparedPolicyAndToolAsync(
        OperationExecutionContext execution, IAiTool? tool, OperationPolicyState policyState, bool argumentsChecked,
        CancellationToken cancellationToken)
    {
        if (!argumentsChecked && RequiresArgumentCheck(execution.Operation.Id, execution.Arguments) && tool is IArgumentCheckTool check)
        {
            var validation = await check.CheckArgumentsAsync(execution.Arguments, execution.Request.UserId, cancellationToken);
            if (validation.IsFailure)
                return await DenyByPolicyAsync(execution, new AgentPolicyDecision(
                    AgentPolicyDecisionStatus.Denied, execution.Capability, validation.ErrorCode ?? validation.Error), cancellationToken);
        }

        var policyDecision = EvaluatePolicy(execution);
        policyState.Decision = policyDecision;

        if (policyDecision.Status == AgentPolicyDecisionStatus.Denied)
            return await DenyByPolicyAsync(execution, policyDecision, cancellationToken);

        if (policyDecision.Status == AgentPolicyDecisionStatus.ConfirmationRequired)
            return await RequireConfirmationAsync(execution, policyDecision, cancellationToken);

        if (execution.Request.PreviewOnly)
            return await DenyByPolicyAsync(execution, new AgentPolicyDecision(
                AgentPolicyDecisionStatus.Denied, execution.Capability, "preview_required"), cancellationToken);

        if (tool is null)
            return AgentOperationResponseFactory.MissingTool(
                execution.Operation.Id,
                execution.Capability.RiskClass,
                execution.Capability.ConfirmationRequirement,
                execution.Summary);

        var executionCancellationToken = cancellationToken;
        if (execution.Request.OnExecutionStarted is not null)
        {
            try
            {
                await execution.Request.OnExecutionStarted();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Agent tool step notification failed for {OperationId}", execution.Operation.Id);
                if (policyDecision.ConsumedConfirmation)
                    executionCancellationToken = CancellationToken.None;
            }
        }

        var result = await ExecuteToolWithConcurrencyRetryAsync(tool, execution, executionCancellationToken);
        if (tool is ITransactionalAiTool && !result.Success)
            throw new ToolOutcomeRollbackException(policyDecision, result);

        return await BuildToolOutcomeResponseAsync(execution, policyDecision, result, executionCancellationToken);
    }

    private static bool RequiresArgumentCheck(string operationId, JsonElement arguments)
    {
        var action = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("action", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        return operationId switch
        {
            "update_checklist_template" => true,
            "update_profile_preferences" => action == "set_name",
            "manage_calendar_sync" => action == "set_selected_calendars",
            _ => false
        };
    }

    private async Task<AgentExecuteOperationResponse> FailOperationAsync(
        OperationExecutionContext execution,
        Exception failure,
        AgentPolicyDecision? policyDecision,
        CancellationToken cancellationToken)
    {
        LogOperationFailed(logger,
            new RedactedOperationException(failure.GetType().FullName ?? failure.GetType().Name, failure.StackTrace),
            execution.Operation.Id, execution.Request.CorrelationId);

        await TryAuditAsync(
            new AuditContext(
                execution.Request,
                execution.Capability.Id,
                execution.Capability.RiskClass,
                AgentPolicyDecisionStatus.Allowed,
                AgentOperationStatus.Failed,
                execution.Summary,
                RedactArguments(execution.Arguments),
                Error: failure.Message,
                ShadowPolicyDecision: policyDecision?.ShadowStatus,
                ShadowReason: policyDecision?.ShadowReason),
            cancellationToken);

        return AgentOperationResponseFactory.Failed(
            execution.Operation.Id,
            execution.Capability.RiskClass,
            execution.Capability.ConfirmationRequirement,
            execution.Summary);
    }

    private async Task<ToolResult> ExecuteToolWithConcurrencyRetryAsync(
        IAiTool tool,
        OperationExecutionContext execution,
        CancellationToken cancellationToken)
    {
        if (tool is not IConcurrencyRetryableTool)
            return await tool.ExecuteAsync(execution.Arguments, execution.Request.UserId, cancellationToken);

        var attempt = 1;
        while (true)
        {
            try
            {
                return await tool.ExecuteAsync(execution.Arguments, execution.Request.UserId, cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxToolConcurrencyAttempts)
            {
                unitOfWork.ResetTracking();
            }

            attempt++;
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
            new AuditContext(
                execution.Request,
                execution.Capability.Id,
                execution.Capability.RiskClass,
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

    private async Task TryAuditAsync(
        AuditContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditService.RecordAsync(new AgentAuditEntry(
                context.Request.UserId,
                context.CapabilityId,
                context.Request.OperationId,
                context.Request.Surface,
                context.Request.AuthMethod,
                context.RiskClass,
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

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Agent operation {OperationId} failed (correlation {CorrelationId})")]
    private static partial void LogOperationFailed(ILogger logger, Exception ex, string operationId, string? correlationId);

    private sealed class OperationPolicyState
    {
        public AgentPolicyDecision? Decision { get; set; }
    }

    private sealed record OperationExecutionContext(
        AgentExecuteOperationRequest Request,
        AgentOperation Operation,
        AgentCapability Capability,
        JsonElement Arguments,
        string Summary,
        AgentConfirmationRequirement? EscalatedRequirement);

    private sealed record AuditContext(
        AgentExecuteOperationRequest Request,
        string CapabilityId,
        AgentRiskClass RiskClass,
        AgentPolicyDecisionStatus PolicyDecision,
        AgentOperationStatus OutcomeStatus,
        string Summary,
        string? RedactedArguments,
        string? TargetId = null,
        string? TargetName = null,
        string? Error = null,
        AgentPolicyDecisionStatus? ShadowPolicyDecision = null,
        string? ShadowReason = null);
}
