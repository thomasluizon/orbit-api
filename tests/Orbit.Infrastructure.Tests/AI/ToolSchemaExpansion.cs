using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Tests.AI;

internal static class ToolSchemaExpansion
{
    internal static JsonElement Expand(JsonElement schema, int maximumHabitDepth = AppConstants.MaxHabitDepth) =>
        JsonSerializer.SerializeToElement(ExpandNode(schema, schema, 0, maximumHabitDepth));

    private static JsonObject ExpandNode(JsonElement schema, JsonElement root, int depth, int maximum)
    {
        var expanded = JsonNode.Parse(ToolSchemaReferences.Resolve(schema, root).GetRawText())!.AsObject();
        expanded.Remove("$defs");
        if (expanded["properties"] is JsonObject properties)
        {
            var habit = properties.ContainsKey("title") && properties.ContainsKey("frequency_unit");
            if (habit) depth++;
            var leaf = habit && depth == maximum;
            if (leaf)
            {
                properties.Remove("sub_habits");
                properties.Remove("parent_habit_id");
                if (expanded["required"] is JsonArray required)
                    foreach (var field in required.Where(field => field!.GetValue<string>() is "sub_habits" or "parent_habit_id").ToArray())
                        required.Remove(field);
            }
            foreach (var property in properties.ToArray())
                properties[property.Key] = ExpandNode(JsonSerializer.SerializeToElement(property.Value), root, depth, maximum);
            if (leaf && properties["suggested_fields"]?["maxItems"] is JsonValue count)
                properties["suggested_fields"]!["maxItems"] = count.GetValue<int>() - 2;
        }
        if (expanded["items"] is JsonObject items)
            expanded["items"] = ExpandNode(JsonSerializer.SerializeToElement(items), root, depth, maximum);
        if (expanded["anyOf"] is JsonArray alternatives)
        {
            for (var index = 0; index < alternatives.Count; index++)
                alternatives[index] = ExpandNode(JsonSerializer.SerializeToElement(alternatives[index]), root, depth, maximum);
            CollapseNullableReference(expanded, alternatives);
        }
        if (expanded["description"] is JsonValue description)
            expanded["description"] = description.GetValue<string>().Replace(
                $" Maximum {AppConstants.MaxHabitDepth} habit levels including the root; the server validates nesting depth.", "", StringComparison.Ordinal);
        return expanded;
    }
    private static void CollapseNullableReference(JsonObject expanded, JsonArray alternatives)
    {
        if (alternatives.Count == 2 && alternatives[1]?["type"]?.ToJsonString() == "\"null\"")
        {
            var value = alternatives[0]!.AsObject();
            if (value["type"] is JsonValue type)
                value["type"] = new JsonArray(type.GetValue<string>(), "null");
            else if (value["type"] is JsonArray types && !types.Any(type => type!.GetValue<string>() == "null"))
                types.Add("null");
            else if (value["anyOf"] is JsonArray nested)
                nested.Add(new JsonObject { ["type"] = "null" });
            if (value["enum"] is JsonArray values && !values.Any(value => value is null)) values.Add((JsonNode?)null);
            expanded.Remove("anyOf");
            foreach (var property in value) expanded[property.Key] = property.Value?.DeepClone();
        }
    }

}
