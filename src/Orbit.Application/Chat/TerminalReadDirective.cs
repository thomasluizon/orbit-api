using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class TerminalReadDirective
{
    public const string CompleteArgument = "orbit_read_complete";

    public const string Instruction = "Set orbit_read_complete to true on every read when those reads fully answer the request. "
        + "Plan independent reads together in one round. A partial page or recovery block is a valid answer; do not fetch more pages unless requested. "
        + "Set it to false for prerequisite lookups, dependent reads or any request that still needs a write or another tool. "
        + "The server composes typed results without another model call when the read plan is complete. "
        + "Emit no framing prose. The typed block is the answer; never repeat its figures, dates or entity facts.";

    public static object WithCompletionArgument(object parameterSchema, string argument = CompleteArgument,
        string description = "True only when all reads in this round fully answer the request; false for prerequisite lookups or any remaining read or write.")
    {
        var schema = JsonSerializer.SerializeToNode(parameterSchema)?.AsObject()
            ?? throw new InvalidOperationException("Read tool parameters must be an object schema.");
        schema["properties"] ??= new JsonObject();
        schema["required"] ??= new JsonArray();
        schema["properties"]!.AsObject().Add(argument, new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = description
        });
        schema["required"]!.AsArray().Add(argument);
        return schema;
    }

    public static bool IsComplete(AiResponse response, string argument = CompleteArgument) => response.ToolCalls is { Count: > 0 } calls
        && calls.All(call => call.Args.ValueKind == JsonValueKind.Object
            && call.Args.TryGetProperty(argument, out var complete) && complete.ValueKind == JsonValueKind.True);

    public static JsonElement ExecutionArguments(JsonElement arguments, string argument = CompleteArgument) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(argument, out _)
            ? JsonSerializer.SerializeToElement(arguments.EnumerateObject()
                .Where(property => property.Name != argument).ToDictionary(property => property.Name, property => property.Value))
            : arguments;
}
