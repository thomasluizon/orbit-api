using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit.Application.Chat.Tools;

internal static class OptionalToolArguments
{
    internal static readonly string[] HabitClearFields = ["description", "emoji", "frequency_unit", "end_date", "due_time"];
    internal static readonly string[] GoalClearFields = ["description", "deadline"];
    internal static readonly string[] BulkHabitClearFields = [.. HabitClearFields, "frequency_quantity", "interval_weeks"];

    internal static string[] ClearableFields(string operationId) => operationId switch
    {
        "update_habit" => HabitClearFields,
        "update_goal" => GoalClearFields,
        "bulk_update_habits" => BulkHabitClearFields,
        _ => []
    };

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
