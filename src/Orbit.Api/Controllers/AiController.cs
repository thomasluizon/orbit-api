using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Chat.Models;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/ai")]
public class AiController(
    IAgentCatalogService catalogService,
    IAgentPolicyEvaluator policyEvaluator,
    IPendingAgentOperationStore pendingOperationStore,
    IPendingClarificationStore pendingClarificationStore,
    IAgentStepUpService stepUpService,
    IAgentAuditService auditService,
    IAgentOperationExecutor operationExecutor,
    IValidator<ResolveClarificationRequest> resolveClarificationValidator) : ControllerBase
{
    [HttpGet("capabilities")]
    public Task<IActionResult> GetCapabilitiesMetadata(CancellationToken cancellationToken) =>
        ReadCatalogAsync(
            AgentCapabilityIds.CatalogCapabilitiesRead,
            nameof(GetCapabilitiesMetadata),
            "Read AI capability catalog",
            catalogService.GetCapabilities,
            cancellationToken);

    [HttpGet("operations")]
    public Task<IActionResult> GetOperationsMetadata(CancellationToken cancellationToken) =>
        ReadCatalogAsync(
            AgentCapabilityIds.CatalogCapabilitiesRead,
            nameof(GetOperationsMetadata),
            "Read AI operation catalog",
            catalogService.GetOperations,
            cancellationToken);

    [HttpGet("data-catalog")]
    public Task<IActionResult> GetUserDataCatalog(CancellationToken cancellationToken) =>
        ReadCatalogAsync(
            AgentCapabilityIds.CatalogDataRead,
            nameof(GetUserDataCatalog),
            "Read AI user-data catalog",
            catalogService.GetUserDataCatalog,
            cancellationToken);

    [HttpGet("surfaces")]
    public Task<IActionResult> GetAppSurfaces(CancellationToken cancellationToken) =>
        ReadCatalogAsync(
            AgentCapabilityIds.CatalogSurfacesRead,
            nameof(GetAppSurfaces),
            "Read AI app surface catalog",
            catalogService.GetSurfaces,
            cancellationToken);

    private async Task<IActionResult> ReadCatalogAsync<TResult>(
        string capabilityId,
        string operationName,
        string description,
        Func<TResult> readCatalog,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var authMethod = HttpContext.User.GetAgentAuthMethod();
        var decision = policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            capabilityId,
            userId,
            AgentExecutionSurface.Metadata,
            authMethod,
            HttpContext.User.GetGrantedAgentScopes(),
            operationName,
            description));

        if (decision.Status != AgentPolicyDecisionStatus.Allowed)
            return Forbid();

        await auditService.RecordAsync(new AgentAuditEntry(
            userId,
            capabilityId,
            operationName,
            AgentExecutionSurface.Metadata,
            authMethod,
            AgentRiskClass.Low,
            AgentPolicyDecisionStatus.Allowed,
            AgentOperationStatus.Succeeded,
            HttpContext.TraceIdentifier,
            description), cancellationToken);

        return Ok(readCatalog());
    }

    public record ConfirmPendingOperationResponse(Guid PendingOperationId, string ConfirmationToken, DateTime ExpiresAtUtc);
    public record StepUpChallengeRequest(string Language = "en");
    public record VerifyStepUpRequest([property: JsonRequired] Guid ChallengeId, string Code);
    public record ExecutePendingOperationRequest(string ConfirmationToken);

    [HttpPost("pending-operations/{id:guid}/confirm")]
    public async Task<IActionResult> ConfirmPendingOperation(Guid id, CancellationToken cancellationToken)
    {
        if (HttpContext.User.GetAgentAuthMethod() == AgentAuthMethod.ApiKey)
            return Forbid();

        var userId = HttpContext.GetUserId();
        var confirmation = pendingOperationStore.Confirm(HttpContext.GetUserId(), id);
        await auditService.RecordAsync(new AgentAuditEntry(
            userId,
            AgentCapabilityIds.ChatInteract,
            nameof(ConfirmPendingOperation),
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            AgentRiskClass.Destructive,
            confirmation is null ? AgentPolicyDecisionStatus.Denied : AgentPolicyDecisionStatus.Allowed,
            confirmation is null ? AgentOperationStatus.Failed : AgentOperationStatus.Succeeded,
            HttpContext.TraceIdentifier,
            "Confirm pending agent operation",
            TargetId: id.ToString(),
            Error: confirmation is null ? "pending_operation_not_found" : null), cancellationToken);

        return confirmation is null
            ? NotFound(ErrorMessages.PendingOperationNotFound.ToErrorBody())
            : Ok(new ConfirmPendingOperationResponse(
                confirmation.PendingOperationId,
                confirmation.ConfirmationToken,
                confirmation.ExpiresAtUtc));
    }

    [HttpPost("pending-operations/{id:guid}/step-up")]
    [DistributedRateLimit("auth")]
    public async Task<IActionResult> MarkPendingOperationStepUp(
        Guid id,
        [FromBody] StepUpChallengeRequest? request,
        CancellationToken cancellationToken)
    {
        if (HttpContext.User.GetAgentAuthMethod() == AgentAuthMethod.ApiKey)
            return Forbid();

        var userId = HttpContext.GetUserId();
        var result = await stepUpService.IssueChallengeAsync(
            userId,
            id,
            request?.Language ?? "en",
            cancellationToken);

        await auditService.RecordAsync(new AgentAuditEntry(
            userId,
            AgentCapabilityIds.ChatInteract,
            nameof(MarkPendingOperationStepUp),
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            AgentRiskClass.High,
            result.IsSuccess ? AgentPolicyDecisionStatus.Allowed : AgentPolicyDecisionStatus.Denied,
            result.IsSuccess ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed,
            HttpContext.TraceIdentifier,
            "Issue step-up challenge for pending agent operation",
            TargetId: id.ToString(),
            Error: result.IsSuccess ? null : result.Error), cancellationToken);

        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpPost("pending-operations/{id:guid}/step-up/verify")]
    [DistributedRateLimit("auth")]
    public async Task<IActionResult> VerifyPendingOperationStepUp(
        Guid id,
        [FromBody] VerifyStepUpRequest request,
        CancellationToken cancellationToken)
    {
        if (HttpContext.User.GetAgentAuthMethod() == AgentAuthMethod.ApiKey)
            return Forbid();

        var userId = HttpContext.GetUserId();
        var result = await stepUpService.VerifyChallengeAsync(
            userId,
            id,
            request.ChallengeId,
            request.Code,
            cancellationToken);

        await auditService.RecordAsync(new AgentAuditEntry(
            userId,
            AgentCapabilityIds.ChatInteract,
            nameof(VerifyPendingOperationStepUp),
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            AgentRiskClass.High,
            result.IsSuccess ? AgentPolicyDecisionStatus.Allowed : AgentPolicyDecisionStatus.Denied,
            result.IsSuccess ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed,
            HttpContext.TraceIdentifier,
            "Verify step-up challenge for pending agent operation",
            TargetId: id.ToString(),
            Error: result.IsSuccess ? null : result.Error), cancellationToken);

        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpPost("pending-operations/{id:guid}/execute")]
    public async Task<IActionResult> ExecutePendingOperation(
        Guid id,
        [FromBody] ExecutePendingOperationRequest request,
        CancellationToken cancellationToken)
    {
        if (HttpContext.User.GetAgentAuthMethod() == AgentAuthMethod.ApiKey)
            return Forbid();

        var userId = HttpContext.GetUserId();
        var pendingExecution = pendingOperationStore.GetExecution(userId, id);
        if (pendingExecution is null)
        {
            await auditService.RecordAsync(new AgentAuditEntry(
                userId,
                AgentCapabilityIds.ChatInteract,
                nameof(ExecutePendingOperation),
                AgentExecutionSurface.Metadata,
                HttpContext.User.GetAgentAuthMethod(),
                AgentRiskClass.High,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Failed,
                HttpContext.TraceIdentifier,
                "Execute pending agent operation",
                TargetId: id.ToString(),
                Error: "pending_operation_not_found"), cancellationToken);

            return NotFound(ErrorMessages.PendingOperationNotFound.ToErrorBody());
        }

        var result = await operationExecutor.ExecuteAsync(new AgentExecuteOperationRequest(
            userId,
            pendingExecution.OperationId,
            pendingExecution.Arguments,
            pendingExecution.Surface,
            HttpContext.User.GetAgentAuthMethod(),
            HttpContext.User.GetGrantedAgentScopes(),
            HttpContext.User.IsReadOnlyCredential(),
            request.ConfirmationToken,
            HttpContext.TraceIdentifier), cancellationToken);

        return Ok(result);
    }

    [HttpPost("clarifications/{operationId:guid}/resolve")]
    [DistributedRateLimit("ai-resolve")]
    public async Task<IActionResult> ResolveClarification(
        Guid operationId,
        [FromBody] ResolveClarificationRequest body,
        CancellationToken cancellationToken)
    {
        var authMethod = HttpContext.User.GetAgentAuthMethod();
        if (authMethod == AgentAuthMethod.ApiKey)
            return Forbid();

        var userId = HttpContext.GetUserId();

        var validation = await resolveClarificationValidator.ValidateAsync(body, cancellationToken);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            return await DenyResolveClarificationAsync(
                userId,
                authMethod,
                operationId,
                $"invalid_clarification_value:{firstError.PropertyName}",
                BadRequest(new AppError(firstError.ErrorCode ?? ErrorCodes.ValidationError, firstError.ErrorMessage).ToErrorBody()),
                cancellationToken);
        }

        var pending = await pendingClarificationStore.GetForResolutionAsync(operationId, userId, cancellationToken);
        if (pending is null)
        {
            return await DenyResolveClarificationAsync(
                userId,
                authMethod,
                operationId,
                "clarification_not_found",
                NotFound(ErrorMessages.ClarificationNotFound.ToErrorBody()),
                cancellationToken);
        }

        if (!pending.AllowedValues.Contains(body.Value, StringComparer.Ordinal))
        {
            return await DenyResolveClarificationAsync(
                userId,
                authMethod,
                operationId,
                "clarification_value_not_offered",
                BadRequest(ErrorMessages.ClarificationValueNotOffered.ToErrorBody()),
                cancellationToken);
        }

        JsonElement mergedArgs;
        try
        {
            mergedArgs = MergeClarificationValue(pending.PartialArgumentsJson, body.Value);
        }
        catch (JsonException)
        {
            return await DenyResolveClarificationAsync(
                userId,
                authMethod,
                operationId,
                "invalid_clarification_value",
                BadRequest(ErrorMessages.ClarificationValueNotJsonObject.ToErrorBody()),
                cancellationToken);
        }

        var claimed = await pendingClarificationStore.MarkResolvedAsync(operationId, userId, cancellationToken);
        if (!claimed)
        {
            var auditError = pending.ExpiresAtUtc <= DateTime.UtcNow
                ? "clarification_expired_mid_flight"
                : "clarification_already_resolved";
            return await DenyResolveClarificationAsync(
                userId,
                authMethod,
                operationId,
                auditError,
                Conflict(ErrorMessages.ClarificationAlreadyResolved.ToErrorBody()),
                cancellationToken);
        }

        return await ExecuteResolvedClarificationAsync(userId, authMethod, operationId, pending, mergedArgs, cancellationToken);
    }

    private async Task<IActionResult> DenyResolveClarificationAsync(
        Guid userId,
        AgentAuthMethod authMethod,
        Guid operationId,
        string auditError,
        IActionResult response,
        CancellationToken cancellationToken)
    {
        await RecordResolveAuditAsync(
            userId,
            authMethod,
            operationId,
            AgentPolicyDecisionStatus.Denied,
            AgentOperationStatus.Failed,
            auditError,
            cancellationToken);

        return response;
    }

    private async Task<IActionResult> ExecuteResolvedClarificationAsync(
        Guid userId,
        AgentAuthMethod authMethod,
        Guid operationId,
        PendingClarificationData pending,
        JsonElement mergedArgs,
        CancellationToken cancellationToken)
    {
        var result = await operationExecutor.ExecuteAsync(new AgentExecuteOperationRequest(
            userId,
            pending.ToolName,
            mergedArgs,
            AgentExecutionSurface.Chat,
            authMethod,
            HttpContext.User.GetGrantedAgentScopes(),
            HttpContext.User.IsReadOnlyCredential(),
            ConfirmationToken: null,
            HttpContext.TraceIdentifier), cancellationToken);

        await RecordResolveAuditAsync(
            userId,
            authMethod,
            operationId,
            result.Operation.Status == AgentOperationStatus.Succeeded
                ? AgentPolicyDecisionStatus.Allowed
                : AgentPolicyDecisionStatus.Denied,
            result.Operation.Status,
            result.Operation.PolicyReason,
            cancellationToken,
            targetName: result.Operation.TargetName);

        return Ok(result);
    }

    private Task RecordResolveAuditAsync(
        Guid userId,
        AgentAuthMethod authMethod,
        Guid operationId,
        AgentPolicyDecisionStatus policyDecision,
        AgentOperationStatus outcome,
        string? error,
        CancellationToken cancellationToken,
        string? targetName = null)
    {
        return auditService.RecordAsync(new AgentAuditEntry(
            userId,
            AgentCapabilityIds.ChatInteract,
            nameof(ResolveClarification),
            AgentExecutionSurface.Chat,
            authMethod,
            AgentRiskClass.Low,
            policyDecision,
            outcome,
            HttpContext.TraceIdentifier,
            "Resolve clarification",
            TargetId: operationId.ToString(),
            TargetName: targetName,
            Error: error), cancellationToken);
    }

    private static JsonElement MergeClarificationValue(string baseJson, string value)
    {
        if (JsonNode.Parse(baseJson) is not JsonObject baseNode)
            throw new JsonException("Stored partial arguments are not a JSON object.");

        if (!string.IsNullOrWhiteSpace(value))
        {
            if (JsonNode.Parse(value) is not JsonObject patchNode)
                throw new JsonException("Clarification value must be a JSON object.");

            DeepMerge(baseNode, patchNode);
        }

        return JsonDocument.Parse(baseNode.ToJsonString()).RootElement.Clone();
    }

    private static void DeepMerge(JsonObject target, JsonObject patch)
    {
        foreach (var kvp in patch.ToList())
        {
            if (target[kvp.Key] is JsonObject targetChild && kvp.Value is JsonObject patchChild)
            {
                DeepMerge(targetChild, patchChild);
            }
            else
            {
                target[kvp.Key] = kvp.Value?.DeepClone();
            }
        }
    }
}
