using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public static class TerminalReadDirective
{
    public const string CompleteArgument = "orbit_read_complete";

    public const string Instruction = "Set orbit_read_complete to true on every read when those reads fully answer the request. "
        + "Plan independent reads together in one round. A partial page or recovery block is a valid answer; do not fetch more pages unless requested. "
        + "For a compound habits-and-goals read, call query_habits and query_goals together and set orbit_read_complete=true on both when they fully answer the request. "
        + "Set it to false for prerequisite lookups, dependent reads or any request that still needs a write or another tool. "
        + "Choosing records requires ask, so query_habits uses for_selection=true and orbit_read_complete=false even for one candidate. "
        + "The server composes typed results without another model call when the read plan is complete. "
        + "Emit no framing prose. The typed block is the answer; never repeat its figures, dates or entity facts.";

    public const string ReadableFallbackInstruction = "The server renders unsupported read blocks and subtypes as readable text from the same typed results. "
        + "Rendering capabilities do not change read-plan completeness. Do not write a fallback answer or leave a complete read plan open because a block kind, subtype or metric is unsupported.";

    public static object WithCompletionArgument(object parameterSchema, string argument = CompleteArgument,
        string description = "True when all reads in this round fully answer the request, including compound reads and readable fallback text; false for prerequisite lookups or any remaining read or write.")
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
        ChatToolControlOrder.Apply(schema);
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
