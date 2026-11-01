using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Tools;

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
        TryGetField(ToolSchemaReferences.Resolve(schema, schema), field, out _);

    public static bool Accepts(JsonElement schema, string field, JsonElement value) =>
        Accepts(schema, field, value, schema);

    private static bool Accepts(JsonElement schema, string field, JsonElement value, JsonElement root) =>
        TryGetField(ToolSchemaReferences.Resolve(schema, root), field, out var declared) && AcceptsValue(declared, field, value, root);

    public static bool IsDateField(string field) =>
        field is "date" or "deadline" || field.EndsWith("_date", StringComparison.Ordinal);

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

    private static bool AcceptsValue(JsonElement schema, string field, JsonElement value, JsonElement root)
    {
        schema = ToolSchemaReferences.Resolve(schema, root);
        if (schema.TryGetProperty("anyOf", out var alternatives))
            return alternatives.EnumerateArray().Any(alternative => AcceptsValue(alternative, field, value, root));
        if (value.ValueKind == JsonValueKind.Null)
            return (schema.TryGetProperty("nullable", out var nullable) && nullable.ValueKind == JsonValueKind.True)
                || (schema.TryGetProperty("type", out var types) && types.ValueKind == JsonValueKind.Array
                    && types.EnumerateArray().Any(type => type.GetString() == "null"))
                || (schema.TryGetProperty("type", out var nullType) && nullType.ValueKind == JsonValueKind.String
                    && nullType.GetString() == "null");

        if (schema.TryGetProperty("enum", out var options)
            && !options.EnumerateArray().Any(option => JsonElement.DeepEquals(option, value)))
            return false;

        var type = schema.TryGetProperty("type", out var declared)
            ? declared.ValueKind == JsonValueKind.String ? declared.GetString()
                : declared.EnumerateArray().Select(value => value.GetString()).FirstOrDefault(value => value != "null")
            : null;
        return type switch
        {
            "string" => value.ValueKind == JsonValueKind.String && HasFormat(field, value.GetString()!),
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array && AcceptsItems(schema, field, value, root),
            "object" => value.ValueKind == JsonValueKind.Object && AcceptsFields(schema, value, root),
            _ => false
        };
    }

    private static bool AcceptsItems(JsonElement schema, string field, JsonElement value, JsonElement root) =>
        schema.TryGetProperty("items", out var items)
            ? value.EnumerateArray().All(item => AcceptsValue(items, field, item, root))
            : value.GetArrayLength() == 0;

    private static bool AcceptsFields(JsonElement schema, JsonElement value, JsonElement root)
    {
        if (schema.TryGetProperty("required", out var required)
            && required.EnumerateArray().Any(name => !value.TryGetProperty(name.GetString()!, out var present)
                || present.ValueKind == JsonValueKind.Null))
            return false;

        return value.EnumerateObject().All(property =>
            Accepts(schema, property.Name, property.Value, root));
    }

    private static bool HasFormat(string field, string value)
    {
        if (field == "days")
            return Enum.TryParse<DayOfWeek>(value, true, out var day) && Enum.IsDefined(day)
                && !int.TryParse(value, out _);
        if (field == "calendar_ids")
            return !string.IsNullOrWhiteSpace(value);
        if (field.EndsWith("_id", StringComparison.Ordinal) || field.EndsWith("_ids", StringComparison.Ordinal))
            return Guid.TryParse(value, out _);
        if (field == "expires_at_utc")
            return !string.IsNullOrWhiteSpace(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out _);
        if (IsDateField(field))
            return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
        if (IsTimeField(field))
            return TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
        return true;
    }
}
