using System.Text.Json;

namespace Orbit.Application.Chat;

internal static class TerminalAnswerDirective
{
    internal const string Instruction = "For this composition client, a tool-free final reply must be a JSON object with aiMessage and kind: "
        + "{\"aiMessage\":\"your answer\",\"kind\":\"answer\"}. Use kind=answer only for a complete answer that needs no input from the person. "
        + "A clarification must never finish as free-text prose or kind=answer. When a habit target or its requested change is missing, "
        + "call query_habits(for_selection=true, orbit_read_complete=false), then ask with form choice; put the whole question in that block and allow free text when the change is missing. "
        + "Other missing input also uses ask after its typed read or bound missing-field result. If input is still needed, use kind=needs_input so the server can continue the turn. "
        + "Tool calls and server-composed read, choice and approval blocks need no final JSON reply.";

    internal const string ValidationFeedback = "The tool-free reply did not establish a complete answer with kind=answer and aiMessage. "
        + "Continue the original request using the declared tools. If input is missing, perform the prerequisite typed read and then use ask; never repeat the question as prose. "
        + "For an answer needing no input, return the required final JSON object. Do not invent a tool result or a successful operation.";

    internal static bool TryRead(string? text, out string? answer)
    {
        answer = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "answer"
                || !root.TryGetProperty("aiMessage", out var message) || message.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(message.GetString())) return false;
            answer = message.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
