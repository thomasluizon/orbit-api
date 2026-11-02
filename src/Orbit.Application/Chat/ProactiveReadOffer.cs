using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit.Application.Chat;

internal static class ProactiveReadOffer
{
    internal const string Argument = "orbit_offer_intent";
    internal const string Instruction = "On a read call, set orbit_offer_intent to aim only when the person expresses an aim and the complete active-goal facts contain no equivalent goal; set dated_event only for an actual dated event, inspect for useful record navigation, otherwise none. This declares an optional block action, never permission to write. An expressed aim beside a read needs no habit lookup or planning call. Complete the requested reads in the same round with orbit_read_complete=true. The server composes the action from successful typed results without a separate selector call.";

    internal static object Schema(object schema)
    {
        var root = JsonSerializer.SerializeToNode(schema)!.AsObject();
        root["properties"]!.AsObject()[Argument] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("aim", "dated_event", "inspect", "none"),
            ["description"] = Instruction
        };
        ChatToolControlOrder.Apply(root);
        return root;
    }

    internal static string? Read(JsonElement args) => args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(Argument, out var intent) && intent.ValueKind == JsonValueKind.String
        && intent.GetString() is "aim" or "dated_event" or "inspect" ? intent.GetString() : null;
}
