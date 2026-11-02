using System.Text.Json.Nodes;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Services;

internal static class ModelToolPayload
{
    public static JsonNode Fit(string json, int maxLength = AppConstants.MaxAiToolPayloadJsonLength)
    {
        var data = JsonNode.Parse(json);
        if (json.Length <= maxLength)
            return data!;

        var originalCounts = Nodes(data).Where(entry => entry.Node is JsonArray)
            .ToDictionary(entry => entry.Path, entry => ((JsonArray)entry.Node).Count);
        var shortened = new HashSet<string>();
        var omitted = 0;
        var envelope = new JsonObject()
        {
            ["truncated"] = true,
            ["original_length"] = json.Length,
            ["data"] = data,
            ["collections"] = new JsonArray(),
            ["shortened_fields"] = new JsonArray(),
            ["omitted_field_count"] = omitted
        };

        while (true)
        {
            envelope["collections"] = new JsonArray(Nodes(data).Where(entry => entry.Node is JsonArray array
                    && originalCounts.GetValueOrDefault(entry.Path) > array.Count)
                .Select(entry => (JsonNode)new JsonObject
                {
                    ["path"] = entry.Path,
                    ["total"] = originalCounts[entry.Path],
                    ["returned"] = ((JsonArray)entry.Node).Count,
                    ["has_more"] = true
                }).Take(20).ToArray());
            envelope["shortened_fields"] = new JsonArray(shortened.Take(20).Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
            envelope["omitted_field_count"] = omitted;
            if (envelope.ToJsonString().Length <= maxLength)
                return envelope;
            var entries = Nodes(data).ToArray();
            var largestArray = entries.Where(entry => entry.Node is JsonArray { Count: > 1 })
                .OrderByDescending(entry => entry.Node.ToJsonString().Length).FirstOrDefault();
            if (largestArray.Node is JsonArray array)
            {
                var retained = Math.Max(1, array.Count / 2);
                while (array.Count > retained) array.RemoveAt(array.Count - 1);
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
                if (ReferenceEquals(data, textNode))
                {
                    envelope["data"] = replacement;
                    data = replacement;
                }
                else textNode.ReplaceWith(replacement);
                shortened.Add(longestText.Path);
                continue;
            }
            var property = entries.LastOrDefault(entry => entry.Node.Parent is JsonObject && !ReferenceEquals(entry.Node, data));
            if (property.Node is null)
            {
                envelope["data"] = null;
                envelope["collections"] = new JsonArray();
                envelope["shortened_fields"] = new JsonArray();
                return envelope;
            }
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
