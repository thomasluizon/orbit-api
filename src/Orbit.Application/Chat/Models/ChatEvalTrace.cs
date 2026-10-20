using System.Text.Json;

namespace Orbit.Application.Chat.Models;

public sealed record ChatEvalTrace(string TraceId, IReadOnlyList<ChatEvalToolCall> ToolCalls);

public sealed record ChatEvalToolCall(
    string Id,
    string Name,
    JsonElement Arguments,
    string Status,
    JsonElement? Result);
