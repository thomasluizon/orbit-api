using System.Text.Json;

namespace Orbit.Domain.Models;

public record AiToolCall(string Name, string Id, JsonElement Args);

public record AiToolCallResult(string Name, string Id, bool Success, string? EntityId, string? EntityName, string? Error);

public record AiResponse
{
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }
    public string? TextMessage { get; init; }
    public bool HasToolCalls => ToolCalls is { Count: > 0 };
}
