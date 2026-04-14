using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Server;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class AgentTools(
    IAgentCatalogService catalogService,
    IAgentOperationExecutor operationExecutor,
    IPendingAgentOperationStore pendingOperationStore,
    IAgentStepUpService stepUpService)
{
    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement.Clone();

    [McpServerTool(Name = "list_agent_capabilities_v2"), Description("Return the Orbit AI capability catalog with risk, scopes, and confirmation requirements.")]
    public IReadOnlyList<AgentCapability> ListCapabilities() => catalogService.GetCapabilities();

    [McpServerTool(Name = "list_agent_operations_v2"), Description("Return the Orbit AI operation catalog, including typed request and response schemas.")]
    public IReadOnlyList<AgentOperation> ListOperations() => catalogService.GetOperations();

    [McpServerTool(Name = "list_app_surfaces_v2"), Description("Return the Orbit app-surface catalog and how-to guidance.")]
    public IReadOnlyList<AppSurface> ListAppSurfaces() => catalogService.GetSurfaces();

    [McpServerTool(Name = "list_user_data_catalog_v2"), Description("Return the Orbit user-data catalog, including sensitivity and AI mutability flags.")]
    public IReadOnlyList<UserDataCatalogEntry> ListUserDataCatalog() => catalogService.GetUserDataCatalog();

    [McpServerTool(Name = "execute_agent_operation_v2"), Description("Execute a typed Orbit AI operation through the shared policy and execution layer.")]
    public Task<AgentExecuteOperationResponse> ExecuteAgentOperation(
        ClaimsPrincipal user,
        [Description("Operation ID from list_agent_operations_v2")] string operationId,
        [Description("JSON object containing the operation arguments")] JsonElement? arguments = null,
        [Description("Confirmation token returned after confirming a pending operation, when required")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        return operationExecutor.ExecuteAsync(new AgentExecuteOperationRequest(
            user.GetUserId(),
            operationId,
            arguments?.Clone() ?? EmptyArguments,
            AgentExecutionSurface.Mcp,
            user.GetAgentAuthMethod(),
            user.GetGrantedAgentScopes(),
            user.IsReadOnlyCredential(),
            confirmationToken), cancellationToken);
    }

    [McpServerTool(Name = "confirm_agent_operation_v2"), Description("Confirm a pending destructive or high-risk Orbit AI operation and receive a short-lived confirmation token.")]
    public PendingAgentOperationConfirmation? ConfirmAgentOperation(
        ClaimsPrincipal user,
        [Description("Pending operation ID")] string pendingOperationId)
    {
        if (user.GetAgentAuthMethod() == AgentAuthMethod.ApiKey)
            throw new UnauthorizedAccessException("API key credentials cannot confirm pending operations.");

        if (!Guid.TryParse(pendingOperationId, out var parsedId))
            throw new ArgumentException("pendingOperationId must be a valid GUID.", nameof(pendingOperationId));

        return pendingOperationStore.Confirm(user.GetUserId(), parsedId);
    }

    [McpServerTool(Name = "step_up_agent_operation_v2"), Description("Request a short-lived email step-up challenge for a pending high-risk Orbit AI operation.")]
    public Task<AgentStepUpChallenge> StepUpAgentOperation(
        ClaimsPrincipal user,
        [Description("Pending operation ID")] string pendingOperationId,
        [Description("Language code for the step-up email")] string language = "en",
        CancellationToken cancellationToken = default)
    {
        if (user.GetAgentAuthMethod() == AgentAuthMethod.ApiKey)
            throw new UnauthorizedAccessException("API key credentials cannot satisfy step-up authorization.");

        if (!Guid.TryParse(pendingOperationId, out var parsedId))
            throw new ArgumentException("pendingOperationId must be a valid GUID.", nameof(pendingOperationId));

        return IssueChallengeAsync(user.GetUserId(), parsedId, language, cancellationToken);
    }

    [McpServerTool(Name = "verify_step_up_agent_operation_v2"), Description("Verify a step-up email challenge for a pending high-risk Orbit AI operation.")]
    public Task<PendingAgentOperation> VerifyStepUpAgentOperation(
        ClaimsPrincipal user,
        [Description("Pending operation ID")] string pendingOperationId,
        [Description("Step-up challenge ID")] string challengeId,
        [Description("6-digit step-up verification code")] string code,
        CancellationToken cancellationToken = default)
    {
        if (user.GetAgentAuthMethod() == AgentAuthMethod.ApiKey)
            throw new UnauthorizedAccessException("API key credentials cannot satisfy step-up authorization.");

        if (!Guid.TryParse(pendingOperationId, out var parsedPendingId))
            throw new ArgumentException("pendingOperationId must be a valid GUID.", nameof(pendingOperationId));

        if (!Guid.TryParse(challengeId, out var parsedChallengeId))
            throw new ArgumentException("challengeId must be a valid GUID.", nameof(challengeId));

        return VerifyChallengeAsync(user.GetUserId(), parsedPendingId, parsedChallengeId, code, cancellationToken);
    }

    private async Task<AgentStepUpChallenge> IssueChallengeAsync(
        Guid userId,
        Guid pendingOperationId,
        string language,
        CancellationToken cancellationToken)
    {
        var result = await stepUpService.IssueChallengeAsync(userId, pendingOperationId, language, cancellationToken);
        if (result.IsFailure)
            throw new InvalidOperationException(result.Error);

        return result.Value;
    }

    private async Task<PendingAgentOperation> VerifyChallengeAsync(
        Guid userId,
        Guid pendingOperationId,
        Guid challengeId,
        string code,
        CancellationToken cancellationToken)
    {
        var result = await stepUpService.VerifyChallengeAsync(userId, pendingOperationId, challengeId, code, cancellationToken);
        if (result.IsFailure)
            throw new InvalidOperationException(result.Error);

        return result.Value;
    }
}
