using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit.Application.Chat.Tools;

public static class OptionalToolArguments
{
    internal static readonly string[] HabitClearFields = ["description", "emoji", "frequency_unit", "end_date", "due_time", "due_end_time", "repeat_reminder"];
    internal static readonly string[] GoalClearFields = ["description", "deadline"];
    internal static readonly string[] BulkHabitClearFields = [.. HabitClearFields, "frequency_quantity", "interval_weeks"];

    internal static string[] ClearableFields(string operationId) => operationId switch
    {
        "update_habit" => HabitClearFields,
        "update_goal" => GoalClearFields,
        "bulk_update_habits" => BulkHabitClearFields,
        _ => []
    };

    /// <summary>
    /// Converts external callers' explicit null clears to the shared tools' clear contract.
    /// Chat placeholder nulls must not pass through this boundary conversion.
    /// </summary>
    public static JsonElement NormalizeExternalClears(string operationId, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return arguments;
        operationId = operationId.ToLowerInvariant();
        var fields = ClearableFields(operationId);
        if (fields.Length == 0 && operationId != "bulk_update_habit_emojis")
            return arguments;
        var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        if (operationId == "bulk_update_habits")
            TranslateBulkNullClears(node, fields);
        else if (operationId == "bulk_update_habit_emojis")
            TranslateEmojiNullClear(node);
        else
            TranslateNullClears(node, fields);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void TranslateBulkNullClears(JsonObject node, string[] fields)
    {
        if (node["updates"] is JsonObject updates)
            TranslateNullClears(updates, fields);
        if (node["revised_items"] is JsonArray items)
            foreach (var item in items.OfType<JsonObject>())
                TranslateNullClears(item, fields);
    }

    private static void TranslateEmojiNullClear(JsonObject node)
    {
        if (!node.ContainsKey("emoji") || node["emoji"] is not null || node["emoji_mode"] is not null)
            return;
        if (node["infer_from_title"] is JsonValue value
            && (!value.TryGetValue<bool>(out var inferFromTitle) || inferFromTitle))
            return;
        node.Remove("emoji");
        node["emoji_mode"] = "clear";
    }

    private static void TranslateNullClears(JsonObject node, string[] fields)
    {
        var nullFields = fields.Where(field => node.ContainsKey(field) && node[field] is null).ToArray();
        if (nullFields.Length == 0 || node["clear_fields"] is not null and not JsonArray)
            return;
        if (node["clear_fields"] is not JsonArray clears)
        {
            clears = [];
            node["clear_fields"] = clears;
        }
        foreach (var field in nullFields)
        {
            node.Remove(field);
            if (!clears.Any(value => value is JsonValue item && item.TryGetValue<string>(out var name) && name == field))
                clears.Add(field);
        }
    }

    public static JsonElement OmitOptionalNulls(JsonElement arguments, object parameterSchema)
    {
        var node = JsonNode.Parse(arguments.GetRawText())!;
        var schema = JsonSerializer.SerializeToElement(parameterSchema);
        OmitOptionalNulls(node, schema, schema);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void OmitOptionalNulls(JsonNode node, JsonElement schema, JsonElement root)
    {
        schema = ToolSchemaReferences.Resolve(schema, root);
        if (node is JsonObject obj && schema.TryGetProperty("properties", out var properties))
            OmitObjectNulls(obj, schema, properties, root);
        if (node is JsonArray array && schema.TryGetProperty("items", out var items))
            foreach (var item in array)
                if (item is not null) OmitOptionalNulls(item, items, root);
        if (schema.TryGetProperty("anyOf", out var alternatives))
            foreach (var alternative in alternatives.EnumerateArray()) OmitOptionalNulls(node, alternative, root);
    }

    private static void OmitObjectNulls(JsonObject obj, JsonElement schema, JsonElement properties, JsonElement root)
    {
        var required = schema.TryGetProperty("required", out var names)
            ? names.EnumerateArray().Select(name => name.GetString()).ToHashSet() : [];
        foreach (var property in obj.ToArray())
        {
            if (!properties.TryGetProperty(property.Key, out var declared)) continue;
            if (property.Value is null)
            {
                if (!required.Contains(property.Key)) obj.Remove(property.Key);
            }
            else OmitOptionalNulls(property.Value, declared, root);
        }
    }

    public static JsonElement OmitNulls(JsonElement arguments)
    {
        if (arguments.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
            return arguments;
        var node = JsonNode.Parse(arguments.GetRawText())!;
        OmitNulls(node);
        return JsonSerializer.SerializeToElement(node);
    }

    public static (JsonElement Arguments, string? Error) ResolveClears(JsonElement arguments, params string[] fields)
    {
        var node = JsonNode.Parse(OmitNulls(arguments).GetRawText())!.AsObject();
        if (node.Remove("clear_fields", out var clears))
        {
            if (clears is not JsonArray list || list.Any(value => value is not JsonValue
                || !value.AsValue().TryGetValue<string>(out var field) || !fields.Contains(field)))
                return (arguments, "clear_fields contains an unsupported field.");
            foreach (var value in list)
            {
                var field = value!.GetValue<string>();
                if (node.ContainsKey(field))
                    return (arguments, $"Cannot set and clear {field} together.");
                node[field] = null;
            }
        }
        return (JsonSerializer.SerializeToElement(node), null);
    }

    public static object ClearFieldsSchema(params string[] fields) => new
    {
        type = JsonSchemaTypes.Array,
        description = "Fields to clear. Null values in other fields leave them unchanged.",
        items = new { type = JsonSchemaTypes.String, @enum = fields }
    };

    private static void OmitNulls(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is null) obj.Remove(property.Key);
                else OmitNulls(property.Value);
            }
        }
        else if (node is JsonArray array)
            foreach (var item in array)
                if (item is not null) OmitNulls(item);
    }
}
