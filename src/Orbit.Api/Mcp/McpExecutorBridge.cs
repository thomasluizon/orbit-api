using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Api.Extensions;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Api.Mcp;

/// <summary>
/// Routes MCP tool mutations through the shared <see cref="IAgentOperationExecutor"/> so they
/// pass the same policy evaluation and <c>AgentAuditLogs</c> trail as every other agent surface.
/// Serializes a caller-supplied snake_case argument object into the <see cref="JsonElement"/> the
/// backing <c>IAiTool</c> expects, builds the request with <see cref="AgentExecutionSurface.Mcp"/>
/// and the four claim-derived credential fields, and maps the executor outcome back to the legacy
/// MCP string contract: callers format their own success message; denials/failures become
/// <c>"Error: …"</c>; pending-confirmation outcomes return a deterministic confirmation prompt.
/// </summary>
public class McpExecutorBridge(IAgentOperationExecutor operationExecutor)
{
    private static readonly JsonSerializerOptions ArgumentSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<McpExecutorResult> ExecuteAsync(
        ClaimsPrincipal user,
        string operationId,
        object arguments,
        string? confirmationToken,
        CancellationToken cancellationToken)
    {
        var argumentsElement = JsonSerializer.SerializeToElement(arguments, ArgumentSerializerOptions);

        var response = await operationExecutor.ExecuteAsync(new AgentExecuteOperationRequest(
            user.GetUserId(),
            operationId,
            argumentsElement,
            AgentExecutionSurface.Mcp,
            user.GetAgentAuthMethod(),
            user.GetGrantedAgentScopes(),
            user.IsReadOnlyCredential(),
            confirmationToken), cancellationToken);

        return Map(response.Operation);
    }

    private static McpExecutorResult Map(AgentOperationResult operation) => operation.Status switch
    {
        AgentOperationStatus.Succeeded => new McpExecutorResult(
            operation.Status, operation.TargetId, operation.TargetName, operation.Payload, Message: string.Empty),
        AgentOperationStatus.PendingConfirmation => new McpExecutorResult(
            operation.Status, operation.TargetId, operation.TargetName, operation.Payload,
            Message: BuildPendingMessage(operation)),
        _ => new McpExecutorResult(
            operation.Status, operation.TargetId, operation.TargetName, operation.Payload,
            Message: $"Error: {operation.PolicyReason ?? operation.Summary ?? "operation failed"}")
    };

    private static string BuildPendingMessage(AgentOperationResult operation)
    {
        var pendingId = operation.PendingOperationId?.ToString() ?? "unknown";

        if (operation.PolicyReason == "step_up_required")
        {
            return "Step-up verification required before this action runs. Request a code via " +
                   $"step_up_agent_operation_v2 for pending operation {pendingId}, verify it with " +
                   "verify_step_up_agent_operation_v2, then retry with the returned confirmation token.";
        }

        return $"Confirmation required before this action runs. Confirm pending operation {pendingId} " +
               "via confirm_agent_operation_v2, then retry with the returned confirmation token.";
    }
}

/// <summary>
/// Outcome of an MCP mutation routed through <see cref="McpExecutorBridge"/>. On success the caller
/// formats its own message from <see cref="TargetId"/>/<see cref="TargetName"/>/<see cref="Payload"/>;
/// otherwise <see cref="Message"/> carries the ready-to-return error or confirmation string.
/// </summary>
public record McpExecutorResult(
    AgentOperationStatus Status,
    string? TargetId,
    string? TargetName,
    object? Payload,
    string Message)
{
    public bool Succeeded => Status == AgentOperationStatus.Succeeded;
}
