using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class TerminalReadDirective
{
    public const string CompleteArgument = "orbit_read_complete";

    public const string Instruction = "Set orbit_read_complete to true on every read in the first round only when those reads fully answer the request. "
        + "Set it to false for prerequisite lookups, dependent reads or any request that still needs a write or another tool. "
        + "The server completes the round only when every call is a successful read with a typed block. "
        + "In the first round, you may write at most one short framing sentence in the current message's language, before any data exists. "
        + "Use no figures, digits, entity facts or claims about the result, even when spelling a number in words. Never repeat the block. "
        + "English: Here is your habit ranking. Portuguese: Aqui está o ranking dos seus hábitos. "
        + "If another round is needed, the server still uses only that first framing sentence beside the blocks.";

    public static object WithCompletionArgument(object parameterSchema)
    {
        var schema = JsonSerializer.SerializeToNode(parameterSchema)?.AsObject()
            ?? throw new InvalidOperationException("Read tool parameters must be an object schema.");
        schema["properties"] ??= new JsonObject();
        schema["required"] ??= new JsonArray();
        schema["properties"]!.AsObject().Add(CompleteArgument, new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = "True only when all reads in this round fully answer the request; false for prerequisite lookups or any remaining read or write."
        });
        schema["required"]!.AsArray().Add(CompleteArgument);
        return schema;
    }

    public static bool IsComplete(AiResponse response) => response.ToolCalls is { Count: > 0 } calls
        && calls.All(call => call.Args.ValueKind == JsonValueKind.Object
            && call.Args.TryGetProperty(CompleteArgument, out var complete) && complete.ValueKind == JsonValueKind.True);

    public static JsonElement ExecutionArguments(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(CompleteArgument, out _)
            ? JsonSerializer.SerializeToElement(arguments.EnumerateObject()
                .Where(property => property.Name != CompleteArgument).ToDictionary(property => property.Name, property => property.Value))
            : arguments;
}
