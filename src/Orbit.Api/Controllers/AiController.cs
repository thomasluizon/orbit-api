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
    public async Task<IActionResult> GetCapabilitiesMetadata(CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var decision = policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.CatalogCapabilitiesRead,
            userId,
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            HttpContext.User.GetGrantedAgentScopes(),
            nameof(GetCapabilitiesMetadata),
            "Read AI capability catalog"));

        if (decision.Status != AgentPolicyDecisionStatus.Allowed)
            return Forbid();

        await auditService.RecordAsync(new AgentAuditEntry(
            userId,
            AgentCapabilityIds.CatalogCapabilitiesRead,
            nameof(GetCapabilitiesMetadata),
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            AgentRiskClass.Low,
            AgentPolicyDecisionStatus.Allowed,
            AgentOperationStatus.Succeeded,
            HttpContext.TraceIdentifier,
            "Read AI capability catalog"), cancellationToken);

        return Ok(catalogService.GetCapabilities());
    }

    [HttpGet("operations")]
    public async Task<IActionResult> GetOperationsMetadata(CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var decision = policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.CatalogCapabilitiesRead,
            userId,
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            HttpContext.User.GetGrantedAgentScopes(),
            nameof(GetOperationsMetadata),
            "Read AI operation catalog"));

        if (decision.Status != AgentPolicyDecisionStatus.Allowed)
            return Forbid();

        await auditService.RecordAsync(new AgentAuditEntry(
            userId,
            AgentCapabilityIds.CatalogCapabilitiesRead,
            nameof(GetOperationsMetadata),
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            AgentRiskClass.Low,
            AgentPolicyDecisionStatus.Allowed,
            AgentOperationStatus.Succeeded,
            HttpContext.TraceIdentifier,
            "Read AI operation catalog"), cancellationToken);

        return Ok(catalogService.GetOperations());
    }

    [HttpGet("data-catalog")]
    public async Task<IActionResult> GetUserDataCatalog(CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var decision = policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.CatalogDataRead,
            userId,
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            HttpContext.User.GetGrantedAgentScopes(),
            nameof(GetUserDataCatalog),
            "Read AI user-data catalog"));

        if (decision.Status != AgentPolicyDecisionStatus.Allowed)
            return Forbid();

        await auditService.RecordAsync(new AgentAuditEntry(
            userId,
            AgentCapabilityIds.CatalogDataRead,
            nameof(GetUserDataCatalog),
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            AgentRiskClass.Low,
            AgentPolicyDecisionStatus.Allowed,
            AgentOperationStatus.Succeeded,
            HttpContext.TraceIdentifier,
            "Read AI user-data catalog"), cancellationToken);

        return Ok(catalogService.GetUserDataCatalog());
    }

    [HttpGet("surfaces")]
    public async Task<IActionResult> GetAppSurfaces(CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var decision = policyEvaluator.Evaluate(new AgentPolicyEvaluationContext(
            AgentCapabilityIds.CatalogSurfacesRead,
            userId,
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            HttpContext.User.GetGrantedAgentScopes(),
            nameof(GetAppSurfaces),
            "Read AI app surface catalog"));

        if (decision.Status != AgentPolicyDecisionStatus.Allowed)
            return Forbid();

        await auditService.RecordAsync(new AgentAuditEntry(
            userId,
            AgentCapabilityIds.CatalogSurfacesRead,
            nameof(GetAppSurfaces),
            AgentExecutionSurface.Metadata,
            HttpContext.User.GetAgentAuthMethod(),
            AgentRiskClass.Low,
            AgentPolicyDecisionStatus.Allowed,
            AgentOperationStatus.Succeeded,
            HttpContext.TraceIdentifier,
            "Read AI app surface catalog"), cancellationToken);

        return Ok(catalogService.GetSurfaces());
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
            ? NotFound(new { error = "Pending operation not found or expired." })
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

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
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

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
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

            return NotFound(new { error = "Pending operation not found or expired." });
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
    [DistributedRateLimit("chat")]
    public async Task<IActionResult> ResolveClarification(
        Guid operationId,
        [FromBody] ResolveClarificationRequest body,
        CancellationToken cancellationToken)
    {
        // Clarification cards are a UI-only flow — API-key clients can't render or tap them.
        // Mirrors the guard on ExecutePendingOperation and the step-up endpoints.
        var authMethod = HttpContext.User.GetAgentAuthMethod();
        if (authMethod == AgentAuthMethod.ApiKey)
            return Forbid();

        var userId = HttpContext.GetUserId();

        var validation = await resolveClarificationValidator.ValidateAsync(body, cancellationToken);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            await RecordResolveAuditAsync(
                userId,
                authMethod,
                operationId,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Failed,
                $"invalid_clarification_value:{firstError.PropertyName}",
                cancellationToken);
            return BadRequest(new { error = firstError.ErrorMessage });
        }

        var pending = await pendingClarificationStore.GetForResolutionAsync(operationId, userId, cancellationToken);
        if (pending is null)
        {
            await RecordResolveAuditAsync(
                userId,
                authMethod,
                operationId,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Failed,
                "clarification_not_found",
                cancellationToken);
            return NotFound(new { error = ErrorMessages.ClarificationNotFound });
        }

        // The patch must be one of the server-offered quick-action values. This is a
        // defense-in-depth check: prevents a malicious client from hand-crafting a patch
        // that overrides fields the contract never said could be changed.
        if (!pending.AllowedValues.Contains(body.Value, StringComparer.Ordinal))
        {
            await RecordResolveAuditAsync(
                userId,
                authMethod,
                operationId,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Failed,
                "clarification_value_not_offered",
                cancellationToken);
            return BadRequest(new { error = ErrorMessages.ClarificationValueNotOffered });
        }

        JsonElement mergedArgs;
        try
        {
            mergedArgs = MergeClarificationValue(pending.PartialArgumentsJson, body.Value);
        }
        catch (JsonException)
        {
            await RecordResolveAuditAsync(
                userId,
                authMethod,
                operationId,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Failed,
                "invalid_clarification_value",
                cancellationToken);
            return BadRequest(new { error = ErrorMessages.ClarificationValueNotJsonObject });
        }

        // Atomic one-shot claim: if this returns false, either another concurrent request
        // already marked the row resolved OR the row expired in the (typically sub-ms)
        // window between Get and MarkResolved. Bail before re-invoking.
        var claimed = await pendingClarificationStore.MarkResolvedAsync(operationId, userId, cancellationToken);
        if (!claimed)
        {
            var auditError = pending.ExpiresAtUtc <= DateTime.UtcNow
                ? "clarification_expired_mid_flight"
                : "clarification_already_resolved";
            await RecordResolveAuditAsync(
                userId,
                authMethod,
                operationId,
                AgentPolicyDecisionStatus.Denied,
                AgentOperationStatus.Failed,
                auditError,
                cancellationToken);
            return Conflict(new { error = ErrorMessages.ClarificationAlreadyResolved });
        }

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
        // Fail closed if the stored args aren't a JSON object — silently coercing to {}
        // would drop the original tool arguments and replay the tool with only the patch.
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

    /// <summary>
    /// Recursively merges <paramref name="patch"/> into <paramref name="target"/>. When a
    /// key exists on both sides and both values are JsonObjects, the merge recurses into
    /// the nested object instead of clobbering it. Other types (arrays, primitives) are
    /// replaced as-is. Patches we currently emit are flat top-level keys, but the deep
    /// merge keeps nested fields in <c>PartialArgumentsJson</c> safe under future patches.
    /// </summary>
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
