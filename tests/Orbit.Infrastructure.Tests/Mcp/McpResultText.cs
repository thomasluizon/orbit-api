using ModelContextProtocol.Protocol;

namespace Orbit.Infrastructure.Tests.Mcp;

internal static class McpResultText
{
    internal static Orbit.Domain.Models.AgentExecuteOperationResponse AsOperation(this CallToolResult result) =>
        System.Text.Json.JsonSerializer.Deserialize<Orbit.Domain.Models.AgentExecuteOperationResponse>(result.AsText(), ModelContextProtocol.McpJsonUtilities.DefaultOptions)!;

    internal static string AsText(this CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(value => value.Text));
}
