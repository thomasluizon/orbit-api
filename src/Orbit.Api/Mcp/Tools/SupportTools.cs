using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class SupportTools(McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "send_support_request"), Description("Send a support request on behalf of the user.")]
    public async Task<CallToolResult> SendSupportRequest(
        ClaimsPrincipal user,
        [Description("Support request subject")] string subject,
        [Description("Support request message body")] string message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "send_support_request", new { subject, message }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text("Support request sent.") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }
}
