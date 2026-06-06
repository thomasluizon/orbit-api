using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// MCP support tools. <c>send_support_request</c> is a mutation, so it routes through
/// <see cref="McpExecutorBridge"/> → <see cref="Orbit.Domain.Interfaces.IAgentOperationExecutor"/>
/// with <see cref="Orbit.Domain.Models.AgentExecutionSurface.Mcp"/> for shared policy evaluation and
/// the <c>AgentAuditLogs</c> trail, forwarding a snake_case argument object matching the backing
/// <c>send_support_request</c> chat tool schema.
/// </summary>
[McpServerToolType]
public class SupportTools(McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "send_support_request"), Description("Send a support request on behalf of the user.")]
    public async Task<string> SendSupportRequest(
        ClaimsPrincipal user,
        [Description("Sender name")] string name,
        [Description("Sender email address")] string email,
        [Description("Support request subject")] string subject,
        [Description("Support request message body")] string message,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "send_support_request", new
        {
            name,
            email,
            subject,
            message
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? "Support request sent." : result.Message;
    }
}
