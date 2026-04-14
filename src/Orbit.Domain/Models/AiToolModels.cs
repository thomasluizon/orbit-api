using System.Text.Json;

namespace Orbit.Domain.Models;

public record AiToolCall(string Name, string Id, JsonElement Args);

public record AiToolCallResult(
    string Name,
    string Id,
    bool Success,
    string? EntityId,
    string? EntityName,
    string? Error,
    object? Payload = null);

/// <summary>
/// Opaque conversation state passed between SendWithToolsAsync and ContinueWithToolResultsAsync.
/// Eliminates the need for mutable instance fields on the service.
/// </summary>
public sealed class AiConversationContext
{
    /// <summary>Opaque message list. Only consumed by AiIntentService.</summary>
    public object Messages { get; init; } = null!;
    /// <summary>Opaque options object. Only consumed by AiIntentService.</summary>
    public object Options { get; init; } = null!;
}

public record AiResponse
{
    public IReadOnlyList<AiToolCall>? ToolCalls { get; init; }
    public string? TextMessage { get; init; }
    public bool HasToolCalls => ToolCalls is { Count: > 0 };
    public AiConversationContext? ConversationContext { get; init; }
}
