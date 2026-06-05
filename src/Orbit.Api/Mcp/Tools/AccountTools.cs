using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// MCP account tools. <c>manage_account</c> is a high-risk mutation (account reset and deletion
/// lifecycle), so it routes through <see cref="McpExecutorBridge"/> →
/// <see cref="Orbit.Domain.Interfaces.IAgentOperationExecutor"/> with
/// <see cref="Orbit.Domain.Models.AgentExecutionSurface.Mcp"/> for shared policy evaluation and the
/// <c>AgentAuditLogs</c> trail; it requires step-up and forwards a confirmation token.
/// </summary>
[McpServerToolType]
public class AccountTools(McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "manage_account"), Description("Manage the user's account: reset the account, request an account-deletion code, or confirm account deletion with a code.")]
    public async Task<string> ManageAccount(
        ClaimsPrincipal user,
        [Description("Action to perform: reset_account, request_deletion, or confirm_deletion")] string action,
        [Description("For confirm_deletion: the deletion code emailed to the user")] string? code = null,
        [Description("Confirmation token from verify_step_up_agent_operation_v2 (required: account changes are high-risk and need step-up)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "manage_account", new
        {
            action,
            code
        }, confirmationToken, cancellationToken);

        return result.Succeeded ? $"Account: {result.TargetName}" : result.Message;
    }
}
