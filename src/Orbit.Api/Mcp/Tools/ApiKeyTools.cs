using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;
using Orbit.Application.ApiKeys.Queries;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// MCP API-key tools. Both route through <see cref="McpExecutorBridge"/> →
/// <see cref="Orbit.Domain.Interfaces.IAgentOperationExecutor"/> with
/// <see cref="Orbit.Domain.Models.AgentExecutionSurface.Mcp"/> for shared policy evaluation and the
/// <c>AgentAuditLogs</c> trail, and both forward the caller's confirmation token. The read routes
/// the same way as the mutation because <c>RequireApiKeyCreationStepUp</c> raises the read's
/// confirmation requirement to a step-up, and a tool with no pending-operation wrapper would then
/// need a grant it could never obtain.
/// </summary>
[McpServerToolType]
public class ApiKeyTools(McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "get_api_keys"), Description("Get the user's API keys (metadata only, no key material). Requires Pro subscription.")]
    public async Task<string> GetApiKeys(
        ClaimsPrincipal user,
        [Description("Confirmation token from verify_step_up_agent_operation_v2, required only while API-key step-up is switched on")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(
            user,
            "get_api_keys",
            new { },
            confirmationToken,
            cancellationToken);

        if (!result.Succeeded)
            return result.Message;

        if (result.Payload is not IReadOnlyList<ApiKeyResponse> keys || keys.Count == 0)
            return "No API keys.";

        var lines = keys.Select(k =>
            $"- {k.Name} ({k.KeyPrefix}...) | id: {k.Id}" +
            (k.IsReadOnly ? " | read-only" : "") +
            (k.IsRevoked ? " | REVOKED" : "") +
            (k.ExpiresAtUtc is not null ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $" | expires {k.ExpiresAtUtc:yyyy-MM-dd}") : "") +
            $" | scopes: {string.Join(", ", k.Scopes)}");

        return $"API keys ({keys.Count}):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "manage_api_keys"), Description("Create or revoke scoped API keys.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK requires individual [Description]-annotated parameters for tool schema generation")]
    public async Task<string> ManageApiKeys(
        ClaimsPrincipal user,
        [Description("Action to perform: create or revoke")] string action,
        [Description("For revoke: the API key ID (GUID)")] string? keyId = null,
        [Description("For create: the key name")] string? name = null,
        [Description("For create: comma-separated scope names")] string? scopes = null,
        [Description("For create: whether the key is read-only")] bool? isReadOnly = null,
        [Description("For create: ISO-8601 UTC expiry timestamp")] string? expiresAtUtc = null,
        [Description("Confirmation token from confirm_agent_operation_v2, after step_up_agent_operation_v2 and verify_step_up_agent_operation_v2 (required: managing API keys is high-risk and needs step-up)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var scopeList = scopes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = await executorBridge.ExecuteAsync(user, "manage_api_keys", new
        {
            action,
            key_id = keyId,
            name,
            scopes = scopeList,
            is_read_only = isReadOnly,
            expires_at_utc = expiresAtUtc
        }, confirmationToken, cancellationToken);

        if (!result.Succeeded)
            return result.Message;

        return action == "create"
            ? $"Created API key '{result.TargetName}' (id: {result.TargetId})"
            : $"Revoked API key {keyId}.";
    }
}
