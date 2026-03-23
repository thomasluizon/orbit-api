using System.Text.Json;

namespace Orbit.Application.Chat.Tools;

public interface IAiTool
{
    string Name { get; }
    string Description { get; }
    object GetParameterSchema();
    Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct);
}

public record ToolResult(bool Success, string? EntityId = null, string? EntityName = null, string? Error = null);
