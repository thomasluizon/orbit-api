using System.Text.Json;

namespace Orbit.Application.Chat;

public sealed record ConversationToolCall(
    string CallId, string Name, JsonElement Arguments, bool Success, string? Error);
