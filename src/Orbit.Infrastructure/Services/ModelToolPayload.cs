using System.Text.Json.Nodes;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Services;

internal static class ModelToolPayload
{
    public static JsonNode Fit(string json)
    {
        var data = JsonNode.Parse(json);
        if (json.Length <= AppConstants.MaxAiToolPayloadJsonLength)
            return data!;

        var originalCounts = Nodes(data).Where(entry => entry.Node is JsonArray)
            .ToDictionary(entry => entry.Path, entry => ((JsonArray)entry.Node).Count);
        var shortened = new HashSet<string>();
        var omitted = 0;
        JsonObject Envelope() => new()
        {
            ["truncated"] = true,
            ["original_length"] = json.Length,
            ["data"] = data?.DeepClone(),
            ["collections"] = new JsonArray(Nodes(data).Where(entry => entry.Node is JsonArray array
                    && originalCounts.GetValueOrDefault(entry.Path) > array.Count)
                .Select(entry => (JsonNode)new JsonObject
                {
                    ["path"] = entry.Path,
                    ["total"] = originalCounts[entry.Path],
                    ["returned"] = ((JsonArray)entry.Node).Count,
                    ["has_more"] = true
                }).Take(20).ToArray()),
            ["shortened_fields"] = new JsonArray(shortened.Take(20).Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()),
            ["omitted_field_count"] = omitted
        };

        while (true)
        {
            var envelope = Envelope();
            if (envelope.ToJsonString().Length <= AppConstants.MaxAiToolPayloadJsonLength)
                return envelope;
            var entries = Nodes(data).ToArray();
            var largestArray = entries.Where(entry => entry.Node is JsonArray { Count: > 1 })
                .OrderByDescending(entry => entry.Node.ToJsonString().Length).FirstOrDefault();
            if (largestArray.Node is JsonArray array)
            {
                array.RemoveAt(array.Count - 1);
                continue;
            }
            var longestText = entries.Where(entry => entry.Node is JsonValue value
                    && value.TryGetValue<string>(out var text) && text.Length > 64)
                .OrderByDescending(entry => entry.Node.ToJsonString().Length).FirstOrDefault();
            if (longestText.Node is JsonValue textNode)
            {
                var text = textNode.GetValue<string>();
                var length = Math.Max(64, text.Length / 2);
                if (char.IsHighSurrogate(text[length - 1])) length--;
                var replacement = JsonValue.Create(text[..length]);
                if (textNode.Parent is null) data = replacement;
                else textNode.ReplaceWith(replacement);
                shortened.Add(longestText.Path);
                continue;
            }
            var property = entries.Last(entry => entry.Node.Parent is JsonObject);
            var parent = (JsonObject)property.Node.Parent!;
            parent.Remove(property.Node.GetPropertyName());
            omitted++;
        }
    }

    private static IEnumerable<(string Path, JsonNode Node)> Nodes(JsonNode? node, string path = "$")
    {
        if (node is null) yield break;
        yield return (path, node);
        if (node is JsonObject obj)
            foreach (var property in obj)
                foreach (var entry in Nodes(property.Value, path + "/" + property.Key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)))
                    yield return entry;
        if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
                foreach (var entry in Nodes(array[index], path + "/" + index))
                    yield return entry;
    }
}
