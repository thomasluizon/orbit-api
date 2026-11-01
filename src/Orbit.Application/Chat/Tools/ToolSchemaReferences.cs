using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Tools;

/// <summary>Compacts exported schemas while retaining bounded tool schemas for server validation.</summary>
public static class ToolSchemaReferences
{
    public static JsonObject Compact(object parameterSchema)
    {
        var root = JsonSerializer.SerializeToNode(parameterSchema)!.AsObject();
        var definitions = root["$defs"]?.AsObject() ?? new JsonObject();
        root.Remove("$defs");
        FactorHabitTree(root, definitions, true);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        Count(root, counts);
        foreach (var definition in definitions.ToArray()) Count(definition.Value!.AsObject(), counts);
        var shared = new Dictionary<string, string>(StringComparer.Ordinal);
        Rewrite(root, definitions, counts, shared, true);
        foreach (var definition in definitions.ToArray())
            Rewrite(definition.Value!.AsObject(), definitions, counts, shared, true);
        if (definitions.Count > 0) root["$defs"] = definitions;
        return root;
    }

    public static JsonElement Resolve(JsonElement schema, JsonElement root)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out var reference))
        {
            var path = reference.GetString()!;
            if (!visited.Add(path)) throw new JsonException("Cyclic schema alias.");
            if (path == "#") schema = root;
            else
            {
                if (!path.StartsWith("#/", StringComparison.Ordinal))
                    throw new JsonException("Only local schema references are supported.");
                schema = root;
                foreach (var segment in path[2..].Split('/'))
                    schema = schema.GetProperty(Uri.UnescapeDataString(segment)
                        .Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            }
        }
        return schema;
    }

    private static void FactorHabitTree(JsonObject schema, JsonObject definitions, bool root)
    {
        if (schema["properties"] is JsonObject properties
            && properties["sub_habits"] is JsonObject children
            && children["items"] is JsonObject child && RepeatsHabitFields(schema, child))
        {
            if (!definitions.ContainsKey("habit"))
            {
                var template = JsonNode.DeepEquals(schema["required"], child["required"]) ? schema : child;
                var habit = (JsonObject)template.DeepClone();
                habit["properties"]!["sub_habits"]!["items"] = Reference("habit");
                AddDepthDescription(habit["properties"]!["sub_habits"]!.AsObject());
                definitions["habit"] = habit;
            }
            if (!root && JsonNode.DeepEquals(schema["required"], definitions["habit"]!["required"]))
            {
                schema.Clear();
                schema["$ref"] = "#/$defs/habit";
                return;
            }
            children["items"] = Reference("habit");
            AddDepthDescription(children);
        }
        foreach (var childSchema in Children(schema)) FactorHabitTree(childSchema, definitions, false);
    }

    private static bool RepeatsHabitFields(JsonObject parent, JsonObject child)
    {
        if (parent["properties"] is not JsonObject fields || child["properties"] is not JsonObject childFields
            || !fields.ContainsKey("frequency_unit") || !fields.ContainsKey("title")) return false;
        var expected = (JsonObject)parent.DeepClone();
        var actual = (JsonObject)child.DeepClone();
        RemoveHierarchyRequirements(expected);
        RemoveHierarchyRequirements(actual);
        expected["properties"]!.AsObject().Remove("sub_habits");
        actual["properties"]!.AsObject().Remove("sub_habits");
        if (!childFields.ContainsKey("sub_habits"))
        {
            expected["properties"]!.AsObject().Remove("parent_habit_id");
            if (expected["properties"]![ProactiveProposal.Argument]?["maxItems"] is JsonValue maximum)
                expected["properties"]![ProactiveProposal.Argument]!["maxItems"] = maximum.GetValue<int>() - 2;
        }
        if (!JsonNode.DeepEquals(expected, actual)) return false;
        return childFields["sub_habits"]?["items"] is not JsonObject descendant || RepeatsHabitFields(child, descendant);
    }

    private static void RemoveHierarchyRequirements(JsonObject schema)
    {
        if (schema["required"] is not JsonArray required) return;
        foreach (var field in required.Where(field => field!.GetValue<string>() is "parent_habit_id" or "sub_habits").ToArray())
            required.Remove(field);
    }

    private static void AddDepthDescription(JsonObject children) =>
        children["description"] = children["description"]!.GetValue<string>()
            + $" Maximum {AppConstants.MaxHabitDepth} habit levels including the root; the server validates nesting depth.";

    private static JsonObject Reference(string name) => new() { ["$ref"] = "#/$defs/" + name };

    private static IEnumerable<JsonObject> Children(JsonObject schema)
    {
        if (schema["properties"] is JsonObject properties)
            foreach (var property in properties) yield return property.Value!.AsObject();
        if (schema["items"] is JsonObject items) yield return items;
        if (schema["anyOf"] is JsonArray alternatives)
            foreach (var alternative in alternatives) yield return alternative!.AsObject();
    }

    private static void Count(JsonObject schema, Dictionary<string, int> counts)
    {
        var key = schema.ToJsonString();
        counts[key] = counts.GetValueOrDefault(key) + 1;
        foreach (var child in Children(schema)) Count(child, counts);
    }

    private static void Rewrite(JsonObject schema, JsonObject definitions, Dictionary<string, int> counts,
        Dictionary<string, string> shared, bool root = false)
    {
        var key = schema.ToJsonString();
        foreach (var child in Children(schema).ToArray()) Rewrite(child, definitions, counts, shared);
        if (root || key.Length < 120 || counts.GetValueOrDefault(key) < 2) return;
        if (!shared.TryGetValue(key, out var name))
        {
            var index = shared.Count + 1;
            do { name = "shared" + index++; } while (definitions.ContainsKey(name));
            shared[key] = name;
            definitions[name] = schema.DeepClone();
        }
        schema.Clear();
        schema["$ref"] = "#/$defs/" + name;
    }
}
