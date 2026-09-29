using System.Globalization;
using System.Text.Json;

namespace Orbit.Application.Chat;

/// <summary>
/// Checks an edited approval value against the parameter schema that the tool gives the model:
/// its type, its null rule, its enum, the date and time formats the tool parses, and the same
/// checks on every list entry and nested field. A value that fails here is a value the tool
/// reads as another value or refuses at execute.
/// </summary>
public static class AgentArgumentSchema
{
    public static bool Declares(JsonElement schema, string field) =>
        TryGetField(schema, field, out _);

    public static bool Accepts(JsonElement schema, string field, JsonElement value) =>
        TryGetField(schema, field, out var declared) && AcceptsValue(declared, field, value);

    public static bool IsDateField(string field) =>
        field == "date" || field.EndsWith("_date", StringComparison.Ordinal);

    public static bool IsTimeField(string field) =>
        field == "time" || field.EndsWith("_time", StringComparison.Ordinal);

    private static bool TryGetField(JsonElement schema, string field, out JsonElement declared)
    {
        declared = default;
        return schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(field, out declared)
            && declared.ValueKind == JsonValueKind.Object;
    }

    private static bool AcceptsValue(JsonElement schema, string field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return schema.TryGetProperty("nullable", out var nullable) && nullable.ValueKind == JsonValueKind.True;

        if (schema.TryGetProperty("enum", out var options)
            && !options.EnumerateArray().Any(option => JsonElement.DeepEquals(option, value)))
            return false;

        var type = schema.TryGetProperty("type", out var declared) && declared.ValueKind == JsonValueKind.String
            ? declared.GetString()
            : null;
        return type switch
        {
            "string" => value.ValueKind == JsonValueKind.String && HasFormat(field, value.GetString()!),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array && AcceptsItems(schema, field, value),
            "object" => value.ValueKind == JsonValueKind.Object && AcceptsFields(schema, value),
            _ => false
        };
    }

    private static bool AcceptsItems(JsonElement schema, string field, JsonElement value) =>
        schema.TryGetProperty("items", out var items)
            ? value.EnumerateArray().All(item => AcceptsValue(items, field, item))
            : value.GetArrayLength() == 0;

    private static bool AcceptsFields(JsonElement schema, JsonElement value)
    {
        if (schema.TryGetProperty("required", out var required)
            && required.EnumerateArray().Any(name => !value.TryGetProperty(name.GetString()!, out var present)
                || present.ValueKind == JsonValueKind.Null))
            return false;

        return value.EnumerateObject().All(property =>
            TryGetField(schema, property.Name, out var declared)
            && AcceptsValue(declared, property.Name, property.Value));
    }

    private static bool HasFormat(string field, string value)
    {
        if (IsDateField(field))
            return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
        if (IsTimeField(field))
            return TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
        return true;
    }
}
