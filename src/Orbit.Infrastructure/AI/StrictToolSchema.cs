using System.Text.Json.Nodes;
using Orbit.Application.Chat;

namespace Orbit.Infrastructure.AI;

internal static class StrictToolSchema
{
    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "type", "description", "title", "properties", "required", "additionalProperties", "items", "enum",
        "anyOf", "$defs", "$ref", "pattern", "format", "multipleOf", "maximum", "exclusiveMaximum",
        "minimum", "exclusiveMinimum", "minItems", "maxItems"
    };

    public static BinaryData Create(string json)
    {
        var schema = JsonNode.Parse(json)!.AsObject();
        Normalize(schema);
        return BinaryData.FromString(schema.ToJsonString());
    }

    private static void Normalize(JsonObject schema, bool optional = false)
    {
        var nullable = schema.Remove("nullable", out var marker) && marker?.GetValue<bool>() == true;
        foreach (var keyword in schema.Select(property => property.Key).Where(key => !SupportedKeywords.Contains(key)).ToArray())
            schema.Remove(keyword);
        if (schema["type"] is JsonValue type && type.GetValue<string>() == "object" && schema["properties"] is null)
            schema["properties"] = new JsonObject();
        if (schema["$defs"] is JsonObject definitions)
            foreach (var definition in definitions) Normalize(definition.Value!.AsObject());
        if (schema["properties"] is JsonObject properties)
        {
            ChatToolControlOrder.Apply(schema);
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
