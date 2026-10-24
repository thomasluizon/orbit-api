using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Orbit.Api.Extensions;
using Orbit.Application.Chat.Tools;
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
            OptionalToolArguments.NormalizeExternalClears(operationId, arguments?.Clone() ?? EmptyArguments),
            AgentExecutionSurface.Mcp,
            user.GetAgentAuthMethod(),
            user.GetGrantedAgentScopes(),
            user.IsReadOnlyCredential(),
            confirmationToken), cancellationToken);
    }

    [McpServerTool(Name = "confirm_agent_operation_v2"), Description("Confirm a pending destructive or high-risk Orbit AI operation and receive a short-lived confirmation token. Available through the Claude connector; manual API keys must confirm in the Orbit app or connect through the Claude connector.")]
    public CallToolResult ConfirmAgentOperation(
        ClaimsPrincipal user,
        [Description("Pending operation ID")] string pendingOperationId)
    {
        if (ApprovalDenial(user) is { } denial)
            return denial;

        if (!Guid.TryParse(pendingOperationId, out var parsedId))
            return Error("invalid_pending_operation_id", "pendingOperationId must be a valid GUID.");

        var confirmation = pendingOperationStore.Confirm(user.GetUserId(), parsedId);
        return confirmation is null
            ? Error("pending_operation_not_found", "The pending operation is unavailable or cannot be confirmed.")
            : Success(confirmation);
    }

    [McpServerTool(Name = "step_up_agent_operation_v2"), Description("Request a short-lived email step-up challenge for a pending high-risk Orbit AI operation. Available through the Claude connector; manual API keys must confirm in the Orbit app or connect through the Claude connector.")]
    public async Task<CallToolResult> StepUpAgentOperation(
        ClaimsPrincipal user,
        [Description("Pending operation ID")] string pendingOperationId,
        [Description("Language code for the step-up email")] string language = "en",
        CancellationToken cancellationToken = default)
    {
        if (ApprovalDenial(user) is { } denial)
            return denial;

        if (!Guid.TryParse(pendingOperationId, out var parsedId))
            return Error("invalid_pending_operation_id", "pendingOperationId must be a valid GUID.");

        var result = await stepUpService.IssueChallengeAsync(user.GetUserId(), parsedId, language, cancellationToken);
        return result.IsFailure ? Error("step_up_failed", result.Error) : Success(result.Value);
    }

    [McpServerTool(Name = "verify_step_up_agent_operation_v2"), Description("Verify a step-up email challenge for a pending high-risk Orbit AI operation. Available through the Claude connector; manual API keys must confirm in the Orbit app or connect through the Claude connector.")]
    public async Task<CallToolResult> VerifyStepUpAgentOperation(
        ClaimsPrincipal user,
        [Description("Pending operation ID")] string pendingOperationId,
        [Description("Step-up challenge ID")] string challengeId,
        [Description("6-digit step-up verification code")] string code,
        CancellationToken cancellationToken = default)
    {
        if (ApprovalDenial(user) is { } denial)
            return denial;

        if (!Guid.TryParse(pendingOperationId, out var parsedPendingId))
            return Error("invalid_pending_operation_id", "pendingOperationId must be a valid GUID.");

        if (!Guid.TryParse(challengeId, out var parsedChallengeId))
            return Error("invalid_challenge_id", "challengeId must be a valid GUID.");

        var result = await stepUpService.VerifyChallengeAsync(user.GetUserId(), parsedPendingId, parsedChallengeId, code, cancellationToken);
        return result.IsFailure ? Error("step_up_failed", result.Error) : Success(result.Value);
    }

    private static CallToolResult? ApprovalDenial(ClaimsPrincipal user)
    {
        if (user.GetAgentAuthMethod() == AgentAuthMethod.ApiKey)
            return Error("interactive_confirmation_required", McpExecutorBridge.InteractiveConfirmationGuidance);

        return user.IsReadOnlyCredential()
            ? Error("read_only_credential", "Read-only credentials cannot approve write operations.")
            : null;
    }

    private static CallToolResult Success<T>(T value) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions) }]
    };

    private static CallToolResult Error(string code, string message)
    {
        var payload = JsonSerializer.SerializeToElement(new { code, message });
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = payload,
            Content = [new TextContentBlock { Text = payload.GetRawText() }]
        };
    }
}
