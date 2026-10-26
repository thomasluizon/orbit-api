using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Application.Chat.Models;

public sealed record ChatEvalTrace(string TraceId, IReadOnlyList<ChatEvalToolCall> ToolCalls,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatTurnTiming? Timing = null);

public sealed record ChatEvalToolCall(
    string Id,
    string Name,
    JsonElement Arguments,
    string Status,
    JsonElement? Result);
