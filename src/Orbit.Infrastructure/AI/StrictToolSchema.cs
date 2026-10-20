using System.Text.Json.Nodes;

namespace Orbit.Infrastructure.AI;

internal static class StrictToolSchema
{
    public static BinaryData Create(string json)
    {
        var schema = JsonNode.Parse(json)!.AsObject();
        Normalize(schema);
        return BinaryData.FromString(schema.ToJsonString());
    }

    private static void Normalize(JsonObject schema, bool optional = false)
    {
        var nullable = schema.Remove("nullable", out var marker) && marker?.GetValue<bool>() == true;
        schema.Remove("sensitive");
        if (schema["properties"] is JsonObject properties)
        {
            var required = schema["required"]?.AsArray().Select(value => value!.GetValue<string>()).ToHashSet()
                ?? [];
            foreach (var (name, value) in properties)
                Normalize(value!.AsObject(), !required.Contains(name));
            schema["required"] = new JsonArray(properties.Select(property => JsonValue.Create(property.Key)).ToArray());
            schema["additionalProperties"] = false;
        }
        if (schema["items"] is JsonObject items)
            Normalize(items);
        if (schema["anyOf"] is JsonArray alternatives)
            foreach (var alternative in alternatives)
                Normalize(alternative!.AsObject());
        if (optional || nullable)
            AllowNull(schema);
    }

    private static void AllowNull(JsonObject schema)
    {
        if (schema["type"] is JsonValue type)
            schema["type"] = new JsonArray(type.GetValue<string>(), "null");
        else if (schema["type"] is JsonArray types && !types.Any(value => value!.GetValue<string>() == "null"))
            types.Add("null");
        else if (schema["anyOf"] is JsonArray alternatives)
            alternatives.Add(new JsonObject { ["type"] = "null" });
        if (schema["enum"] is JsonArray values && !values.Any(value => value is null))
            values.Add((JsonNode?)null);
    }
}
