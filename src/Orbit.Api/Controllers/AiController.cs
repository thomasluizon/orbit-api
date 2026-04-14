using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
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
    IAgentStepUpService stepUpService,
    IAgentAuditService auditService,
    IAgentOperationExecutor operationExecutor) : ControllerBase
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
}
