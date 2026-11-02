using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit.Application.Chat;

internal static class ProactiveReadOffer
{
    internal const string Argument = "orbit_offer_intent";
    internal const string Instruction = "On complete reads, declare orbit_offer_intent: aim only for an expressed aim without an equivalent active goal in supplied facts; dated_event for an actual dated event; inspect for navigation; otherwise none. This offers an action, never authorizes writes. Answer only the requested reads together with orbit_read_complete=true; an aim alone needs no habit lookup or planning.";

    internal static object Schema(object schema)
    {
        var root = JsonSerializer.SerializeToNode(schema)!.AsObject();
        root["properties"]!.AsObject()[Argument] = new JsonObject
        {
            ["type"] = "string"
        };
        ChatToolControlOrder.Apply(root);
        return root;
    }

    internal static string? Read(JsonElement args) => args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(Argument, out var intent) && intent.ValueKind == JsonValueKind.String
        && intent.GetString() is "aim" or "dated_event" or "inspect" ? intent.GetString() : null;
}
