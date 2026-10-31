using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Habits.Commands;
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
    ILogger<AgentOperationExecutor> logger, ReadBlockFactory? readBlocks = null) : IAgentOperationExecutor, Orbit.Application.Chat.Commands.IAgentMutationRunner
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
            return await DenyUnknownOperationAsync(request, arguments);

        var offerArguments = await PrepareOfferArgumentsAsync(request, operation, arguments, cancellationToken);
        if (offerArguments.IsFailure)
            return new RepairableArgumentResponse(new(new(operation.Id, operation.Id, operation.RiskClass,
                operation.ConfirmationRequirement, AgentOperationStatus.Failed, PolicyReason: offerArguments.Error,
                ErrorCode: offerArguments.ErrorCode, Access: "write")));
        arguments = offerArguments.Value;

        var declaredCapability = catalogService.GetCapability(operation.CapabilityId)
            ?? throw new InvalidOperationException($"Operation '{operation.Id}' is mapped to an unknown capability '{operation.CapabilityId}'.");

        var escalatedRequirement = await stepUpAuthorizationBridge.GetRequiredConfirmationAsync(
            declaredCapability.Id,
            cancellationToken);
        escalatedRequirement = await ChangeSetConfirmationAsync(request, arguments, escalatedRequirement, cancellationToken);
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
            return await DenyDirectUserFlowAsync(execution);

        var ownershipDenialReason = await targetOwnershipService.GetDenialReasonAsync(
            operation.Id,
            request.UserId,
            arguments,
            cancellationToken);

        if (ownershipDenialReason is not null)
            return await DenyOwnershipAsync(execution, ownershipDenialReason);

        if (operation.IsMutation && HasUnsupportedPlacementApproval(request, arguments))
            return await DenyByPolicyAsync(execution, new AgentPolicyDecision(
                AgentPolicyDecisionStatus.Denied, capability, "placement_not_supported"));

        if (operation.IsMutation && request.ConfirmationToken is { } token)
        {
            var confirmed = pendingOperationStore.GetConfirmedExecution(request.UserId, capability.Id,
                AgentOperationFingerprint.Compute(operation.Id, arguments.GetRawText()), token);
            if (confirmed?.RequiresReproposal == true)
                return await DenyByPolicyAsync(execution, new AgentPolicyDecision(
                    AgentPolicyDecisionStatus.Denied, capability, "pending_operation_schema_changed"));
            if (confirmed is not null && request.Surface == AgentExecutionSurface.Chat)
            {
                var current = await changePreviewer.TryPreviewAsync(request.UserId, operation.Id,
                    arguments, logger, cancellationToken, request.ClientContext);
                if (string.IsNullOrWhiteSpace(confirmed.PreviewFingerprint) || current.IsFailure
                    || current.Value?.Items is not { Count: > 0 }
                    || current.Value.PreviewFingerprint != confirmed.PreviewFingerprint)
                    return await DenyByPolicyAsync(execution, new AgentPolicyDecision(
                        AgentPolicyDecisionStatus.Denied, capability, "stale_preview"));
            }
        }
        return await ExecuteWithPolicyAsync(execution, cancellationToken);
    }

    private async Task<Result<JsonElement>> PrepareOfferArgumentsAsync(AgentExecuteOperationRequest request,
        AgentOperation operation, JsonElement arguments, CancellationToken ct)
    {
        if (!operation.IsMutation || !ProactiveProposal.Contains(arguments)) return Result.Success(arguments);
        var offered = await changePreviewer.PreviewAsync(request.UserId, operation.Id, arguments, ct, request.ClientContext);
        if (offered.IsFailure)
            return Result.Failure<JsonElement>(offered.Error, offered.ErrorCode ?? "invalid_offer");
        if (offered.Value?.ExecutionArguments is not { } effective)
            return Result.Failure<JsonElement>("Cannot prepare suggested_fields for this operation; check its fields and target.", "invalid_offer");
        if (toolRegistry.GetTool(operation.Id) is IArgumentCheckTool check)
        {
            var validation = await check.CheckArgumentsAsync(effective, request.UserId, ct);
            if (validation.IsFailure) return Result.Failure<JsonElement>(validation.ErrorCode is { } code ? $"{code}: {validation.Error}" : validation.Error, validation.ErrorCode ?? "invalid_arguments");
        }
        return Result.Success(effective);
    }

    private static bool HasUnsupportedPlacementApproval(AgentExecuteOperationRequest request, JsonElement arguments) =>
        request.Surface == AgentExecutionSurface.Chat && request.ConfirmationToken is not null
        && request.ClientContext?.SupportsPendingOperationPlacement != true
        && PendingOperationPlacementNegotiation.RequiresSupport(request.OperationId, arguments);

    private static bool AsksClarificationFirst(IAiTool? tool, JsonElement arguments) =>
        tool is IClarificationPrecheckTool precheck && precheck.NeedsClarification(arguments);

    private async Task<AgentExecuteOperationResponse> DenyUnknownOperationAsync(
        AgentExecuteOperationRequest request,
        JsonElement arguments)
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
                Error: "unsupported_by_policy"));

        var tool = toolRegistry.GetTool(request.OperationId);
        var capability = catalogService.GetCapabilityByChatTool(request.OperationId);
        var access = tool is not null ? tool.IsReadOnly ? "read" : "write"
            : capability is not null ? capability.IsMutation ? "write" : "read" : null;
        return AgentOperationResponseFactory.UnknownOperation(request.OperationId, access);
    }

    private async Task<AgentExecuteOperationResponse> DenyDirectUserFlowAsync(
        OperationExecutionContext execution)
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
                Error: "direct_user_flow_required"));

        return DeniedResponse(execution, summary, "direct_user_flow_required", "direct_user_flow_required");
    }

    private async Task<AgentExecuteOperationResponse> DenyOwnershipAsync(
        OperationExecutionContext execution,
        string denialReason)
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
                Error: denialReason));

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
        AgentPolicyDecision policyDecision)
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
                ShadowReason: policyDecision.ShadowReason));

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
                ShadowReason: policyDecision.ShadowReason));

        var pending = policyDecision.PendingOperation;
        if (pending is not null && execution.Request.Surface == AgentExecutionSurface.Chat
            && execution.Operation.IsMutation)
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
            if (execution.Request.ConfirmationToken is null && BulkNoChangesOutcome.Supports(execution.Operation.Id)
                && preview is { ChangeTargetCount: 0, Items.Count: 0 })
            {
                pendingOperationStore.Cancel(execution.Request.UserId, pending.Id,
                    AgentOperationFingerprint.Compute(execution.Operation.Id, execution.Arguments.GetRawText()));
                return await BuildToolOutcomeResponseAsync(execution, policyDecision,
                    new ToolResult(true, Payload: BulkNoChangesOutcome.For(execution.Operation.Id, execution.Arguments)),
                    cancellationToken);
            }

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
            execution.Capability.IsMutation,
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
                execution.Arguments, logger, cancellationToken, execution.Request.ClientContext);
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
            if (tool is not ITransactionalAiTool && execution.Operation.Id != AccountChangeSet.OperationName)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AuditFailureAsync(execution, "caller_canceled", policyState.Decision);
            throw;
        }
        catch (Exception ex)
        {
            return await FailOperationAsync(execution, ex, policyState.Decision);
        }
    }

    private async Task<AgentExecuteOperationResponse> ExecutePolicyAndToolAsync(
        OperationExecutionContext execution,
        IAiTool? tool,
        OperationPolicyState policyState,
        CancellationToken cancellationToken)
    {
        policyState.Decision = null;
        if (execution.Request.Surface == AgentExecutionSurface.Chat && tool is IHabitCreationCapacityTool creation)
        {
            var capacity = await creation.CheckCreationCapacityAsync(execution.Arguments, execution.Request.UserId, cancellationToken);
            if (capacity.IsFailure)
                return await BuildToolOutcomeResponseAsync(execution,
                    new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, execution.Capability),
                    ToolResult.FromFailure(capacity), cancellationToken);
        }
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
            {
                if (execution.Operation.Id == "assign_tags")
                    return new RepairableArgumentResponse(await BuildToolOutcomeResponseAsync(execution,
                        new AgentPolicyDecision(AgentPolicyDecisionStatus.Allowed, execution.Capability),
                        ToolResult.FromFailure(validation), cancellationToken));
                return await DenyByPolicyAsync(execution, new AgentPolicyDecision(
                    AgentPolicyDecisionStatus.Denied, execution.Capability, validation.ErrorCode ?? validation.Error));
            }
        }

        IReadOnlyList<OperationExecutionContext>? changeSetMembers = null;
        if (execution.Operation.Id == AccountChangeSet.OperationName)
        {
            var prepared = await PrepareChangeSetAsync(execution, cancellationToken);
            if (prepared.Error is { } error)
                return await DenyByPolicyAsync(execution, new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, execution.Capability, error));
            changeSetMembers = prepared.Members;
        }

        var policyDecision = EvaluatePolicy(execution);
        policyState.Decision = policyDecision;

        if (policyDecision.Status == AgentPolicyDecisionStatus.Denied)
            return await DenyByPolicyAsync(execution, policyDecision);

        if (policyDecision.Status == AgentPolicyDecisionStatus.ConfirmationRequired)
            return await RequireConfirmationAsync(execution, policyDecision, cancellationToken);

        if (execution.Request.PreviewOnly)
            return await DenyByPolicyAsync(execution, new AgentPolicyDecision(
                AgentPolicyDecisionStatus.Denied, execution.Capability, "preview_required"));

        if (changeSetMembers is not null)
        {
            if (!policyDecision.ConsumedConfirmation)
                return await DenyByPolicyAsync(execution, new AgentPolicyDecision(AgentPolicyDecisionStatus.Denied, execution.Capability, "preview_required"));
            var applied = await ExecuteChangeSetAsync(execution, changeSetMembers, cancellationToken);
            if (!applied.Success) throw new ToolOutcomeRollbackException(policyDecision, applied);
            return await BuildToolOutcomeResponseAsync(execution, policyDecision, applied, cancellationToken);
        }

        if (tool is null)
            return AgentOperationResponseFactory.MissingTool(
                execution.Operation.Id,
                execution.Capability.IsMutation,
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

        if (readBlocks is not null && tool.IsReadOnly && ResolvesReadArguments(tool.Name))
            execution = execution with
            {
                Arguments = await readBlocks.ResolveAsync(tool.Name,
                execution.Arguments, execution.Request.UserId, executionCancellationToken)
            };
        var result = await ExecuteToolWithConcurrencyRetryAsync(tool, execution, executionCancellationToken);
        if (tool is ITransactionalAiTool && !result.Success)
            throw new ToolOutcomeRollbackException(policyDecision, result);

        return await BuildToolOutcomeResponseAsync(execution, policyDecision, result, executionCancellationToken);
    }

    private static bool ResolvesReadArguments(string tool) =>
        ReadBlockFactory.Supports(tool) || RelativeReadPeriod.Supports(tool);

    private static bool RequiresArgumentCheck(string operationId, JsonElement arguments)
    {
        var action = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("action", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        return operationId switch
        {
            "bulk_reschedule_habits" => arguments.TryGetProperty("revised_items", out _),
            "update_checklist" or "update_checklist_template" or "assign_tags" => true,
            "update_profile_preferences" => action == "set_name",
            "manage_calendar_sync" => action == "set_selected_calendars",
            _ => false
        };
    }

    private async Task<AgentExecuteOperationResponse> FailOperationAsync(
        OperationExecutionContext execution,
        Exception failure,
        AgentPolicyDecision? policyDecision)
    {
        LogOperationFailed(logger,
            new RedactedOperationException(failure.GetType().FullName ?? failure.GetType().Name, failure.StackTrace),
            execution.Operation.Id, execution.Request.CorrelationId);

        await AuditFailureAsync(execution, failure.Message, policyDecision);

        return AgentOperationResponseFactory.Failed(
            execution.Operation.Id,
            execution.Capability.IsMutation,
            execution.Capability.RiskClass,
            execution.Capability.ConfirmationRequirement,
            execution.Summary);
    }

    private Task AuditFailureAsync(OperationExecutionContext execution, string error,
        AgentPolicyDecision? policyDecision) =>
        TryAuditAsync(
            new AuditContext(
                execution.Request,
                execution.Capability.Id,
                execution.Capability.RiskClass,
                AgentPolicyDecisionStatus.Allowed,
                AgentOperationStatus.Failed,
                execution.Summary,
                RedactArguments(execution.Arguments),
                Error: error,
                ShadowPolicyDecision: policyDecision?.ShadowStatus,
                ShadowReason: policyDecision?.ShadowReason));

    private async Task<ToolResult> ExecuteToolWithConcurrencyRetryAsync(
        IAiTool tool,
        OperationExecutionContext execution,
        CancellationToken cancellationToken)
    {
        if (tool is Orbit.Application.Chat.Tools.Implementations.RevisePendingChangesTool revisionTool)
            return await revisionTool.ExecuteAsync(execution.Arguments, execution.Request.UserId, cancellationToken, execution.Request.ClientContext);
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

        if (!result.Success)
            LogToolOutcomeFailed(logger, execution.Operation.Id, result.ErrorCode ?? "tool_rejected", execution.Request.CorrelationId);
        if (result.Payload is BulkCreateResult bulk)
            foreach (var item in bulk.Results.Where(item => item.Status == BulkItemStatus.Failed))
                LogBulkItemFailed(logger, execution.Operation.Id, item.Index, item.ErrorCode ?? "item_rejected", execution.Request.CorrelationId);

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
                policyDecision.ShadowReason));

        var response = AgentOperationResponseFactory.ToolOutcome(
            execution.Operation.Id,
            execution.Capability.IsMutation,
            execution.Capability.RiskClass,
            execution.Capability.ConfirmationRequirement,
            execution.Summary,
            result,
            outcomeStatus,
            isPayGateDenial);
        if (execution.Request.Surface == AgentExecutionSurface.Chat && result.Payload is BulkCreateResult { Partial: true } partial)
            response = response with { PendingOperation = await CreateBulkRetryAsync(execution, partial, cancellationToken) };
        if (result.Success && !execution.Operation.IsMutation && readBlocks is not null
            && ReadBlockFactory.Supports(execution.Operation.Id, ProactiveProposal.Enabled(execution.Request.ClientContext)))
        {
            var block = readBlocks.Capture(execution.Operation.Id, execution.Arguments, execution.Request.UserId, result.Payload);
            response = response with { Operation = response.Operation with { ReadBlock = block } };
        }
        return response;
    }

    private async Task<PendingAgentOperation?> CreateBulkRetryAsync(
        OperationExecutionContext execution, BulkCreateResult result, CancellationToken cancellationToken)
    {
        if (execution.Operation.Id != "bulk_create_habits"
            || !execution.Arguments.TryGetProperty("habits", out var habits))
            return null;
        var root = JsonNode.Parse(execution.Arguments.GetRawText())!.AsObject();
        root["habits"] = new JsonArray(result.Results.Where(item => item.Status == BulkItemStatus.Failed)
            .Select(item => JsonNode.Parse(habits[item.Index].GetRawText())).ToArray());
        var arguments = JsonSerializer.SerializeToElement(root);
        var pending = pendingOperationStore.Create(execution.Request.UserId, execution.Capability,
            execution.Operation.Id, arguments.GetRawText(), execution.Summary,
            AgentOperationFingerprint.Compute(execution.Operation.Id, arguments.GetRawText()), execution.Request.Surface);
        var retry = await RequireConfirmationAsync(execution with { Arguments = arguments },
            new AgentPolicyDecision(AgentPolicyDecisionStatus.ConfirmationRequired, execution.Capability,
                PendingOperation: pending), cancellationToken);
        return retry.PendingOperation;
    }

    private static AgentExecuteOperationResponse DeniedResponse(
        OperationExecutionContext execution,
        string summary,
        string? policyReason,
        string denialReason)
    {
        return AgentOperationResponseFactory.Denied(
            execution.Operation.Id,
            execution.Capability.IsMutation,
            execution.Capability.RiskClass,
            execution.Capability.ConfirmationRequirement,
            summary,
            policyReason,
            denialReason);
    }

    private IReadOnlyList<string> GetGrantedScopes(AgentExecuteOperationRequest request)
    {
        if (request.AuthMethod is AgentAuthMethod.ApiKey or AgentAuthMethod.OAuth)
            return request.GrantedScopes ?? [];

        if (request.GrantedScopes is { Count: > 0 })
            return request.GrantedScopes;

        return catalogService.GetCapabilities()
            .Select(item => item.Scope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task TryAuditAsync(
        AuditContext context)
    {
        using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
                context.ShadowReason), auditTimeout.Token);
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

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Agent operation {OperationId} failed with unexpected_error (correlation {CorrelationId})")]
    private static partial void LogOperationFailed(ILogger logger, Exception ex, string operationId, string? correlationId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Agent operation {OperationId} rejected with {ReasonCode} (correlation {CorrelationId})")]
    private static partial void LogToolOutcomeFailed(ILogger logger, string operationId, string reasonCode, string? correlationId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Agent operation {OperationId} item {ItemIndex} failed with {ReasonCode} (correlation {CorrelationId})")]
    private static partial void LogBulkItemFailed(ILogger logger, string operationId, int itemIndex, string reasonCode, string? correlationId);

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
