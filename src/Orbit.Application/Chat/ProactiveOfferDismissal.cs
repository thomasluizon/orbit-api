using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

internal static class ProactiveOfferDismissal
{
    internal const string Argument = "orbit_dismissed_offer_keys";
    internal const string Instruction = "When the person dismisses an optional action, include its exact key from previously_issued_offers in orbit_dismissed_offer_keys on your tool call or tool-free final JSON answer. Use an empty array when none were dismissed. Never infer dismissal from account labels or invent a key. A dismissal alone needs no account read or write.";

    internal static object Schema(object schema)
    {
        var root = JsonSerializer.SerializeToNode(schema)!.AsObject();
        root["properties"] ??= new JsonObject();
        root["properties"]!.AsObject()[Argument] = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = 100,
            ["description"] = "Exact keys from previously_issued_offers dismissed by the person in this turn. Empty when none.",
            ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 200 }
        };
        return root;
    }

    internal static IReadOnlyList<string> Read(AiResponse response)
    {
        var keys = response.ToolCalls?.SelectMany(call => Read(call.Args)).ToList() ?? [];
        if (TerminalAnswerDirective.TryRead(response.TextMessage, out _))
        {
            using var answer = JsonDocument.Parse(response.TextMessage!);
            keys.AddRange(Read(answer.RootElement));
        }
        return keys.Distinct(StringComparer.Ordinal).Take(100).ToArray();
    }

    private static IEnumerable<string> Read(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(Argument, out var keys) && keys.ValueKind == JsonValueKind.Array
            ? keys.EnumerateArray().Where(key => key.ValueKind == JsonValueKind.String)
                .Select(key => key.GetString()!).Where(key => key.Length is > 0 and <= 200) : [];
}
