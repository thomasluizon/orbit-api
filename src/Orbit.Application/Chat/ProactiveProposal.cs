using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;
using Orbit.Domain.Common;
using Orbit.Application.Chat.Tools;

namespace Orbit.Application.Chat;

public static class ProactiveProposal
{
    public const string Argument = "suggested_fields";
    internal const string Metadata = "orbit_offer_metadata";
    private const string Properties = "properties";
    public const string Instruction = "For offer-enabled clients, put useful unrequested configuration in suggested_fields, separately from requested fields. Consider repeat reminders for activities repeated throughout the day, reminders, schedule and frequency, checklist steps, independently tracked children or a fitting owned parent, goal links, tags, emoji and a bounded end date. Only propose fields this operation supports and omit irrelevant options. Respect the account plan and capability availability; never propose locked features. Never replace requested or stored values, invent a daily target from an interval, or add end dates to OneTime or General items. Keep doctor-prescribed fixed times in scheduled_reminders; neither fixed medication times nor ordinary daily reading calls for a repeat reminder. Resolve existing parents, goals and tags through account reads. Preserve stored checklist entries, tags and goal links when adding. A goal may need a target, unit, deadline or habit links; template reuse needs a grounded relationship and full contents before editing. Each suggestion stays editable and removable in the same approval preview. Nested drafts use their own suggested_fields; revise the exact slash-separated field path shown in the preview. Corrections preserve all other fields and never regenerate rejected suggestions. Do not add habit configuration to log, skip, delete, reorder, account reset, billing, API-key, support or administrator operations.";

    public static bool Enabled(AgentClientContext? client) => client is
    {
        SupportsProactiveOffers: true, SupportsParts: true, SupportsPendingOperationChanges: true,
        ConversationId: { } id
    } && id != Guid.Empty;

    public static bool Supports(string operation) => operation is "create_habit" or "create_sub_habit"
        or "bulk_create_habits" or "update_habit" or "bulk_update_habits" or "create_goal" or "update_goal"
        or "create_checklist_template" or "update_checklist_template";

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "description", "emoji", "frequency_unit", "frequency_quantity", "interval_weeks", "days",
        "due_date", "start_date", "due_time", "due_end_time", "end_date", "reminder_enabled",
        "reminder_times", "scheduled_reminders", "relative_reminders", "repeat_reminder", "slip_alert_enabled",
        "parent_habit_id", "tag_ids", "tag_names", "goal_ids", "checklist_items", "sub_habits",
        "target_value", "unit", "deadline", "habit_ids", "items", "is_general", "is_flexible"
    };

    internal static bool Eligible(string field) => Fields.Contains(field);

    public static object RevisionSchema(object schema, bool enabled)
    {
        if (enabled) return schema;
        var root = JsonSerializer.SerializeToNode(schema)!.AsObject();
        (root[Properties]?[ToolArgumentNames.Items]?[ToolArgumentNames.Items]?[Properties] as JsonObject)?.Remove("remove_suggested_fields");
        return root;
    }

    public static object Schema(object schema)
    {
        var root = JsonSerializer.SerializeToNode(schema)!.AsObject();
        AddSchema(root);
        return root;
    }

    private static void AddSchema(JsonObject schema)
    {
        if (schema[Properties] is not JsonObject properties) return;
        foreach (var name in new[] { "habits", "sub_habits" })
            if (properties[name]?[ToolArgumentNames.Items] is JsonObject item) AddSchema(item);
        if (properties[ToolArgumentNames.Updates] is JsonObject updates) AddSchema(updates);
        if (properties.ContainsKey("habits") || properties.ContainsKey(ToolArgumentNames.Updates)) return;
        var suggested = new JsonObject();
        foreach (var property in properties.Where(property => Fields.Contains(property.Key)).ToArray())
            suggested[property.Key] = property.Value?.DeepClone();
        if (suggested.Count == 0) return;
        properties[Argument] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = Instruction,
            [Properties] = suggested,
            ["additionalProperties"] = false
        };
    }

    public static bool Contains(JsonElement args) => ContainsNode(JsonNode.Parse(args.GetRawText()));
    private static bool ContainsNode(JsonNode? node) => node switch
    {
        JsonObject obj => obj.ContainsKey(Argument) || obj.ContainsKey(Metadata) || obj.Any(p => ContainsNode(p.Value)),
        JsonArray array => array.Any(ContainsNode),
        _ => false
    };

    public static JsonElement ForModelCall(JsonElement args)
    {
        var node = JsonNode.Parse(args.GetRawText());
        Strip(node, suggestions: false);
        return JsonSerializer.SerializeToElement(node);
    }

    public static JsonElement StripMetadata(JsonElement args)
    {
        var node = JsonNode.Parse(args.GetRawText());
        Strip(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Strip(JsonNode? node, bool suggestions = true)
    {
        if (node is JsonObject obj)
        {
            obj.Remove(Metadata);
            if (suggestions) obj.Remove(Argument);
            foreach (var property in obj.ToArray()) Strip(property.Value, suggestions);
        }
        else if (node is JsonArray array) foreach (var item in array) Strip(item, suggestions);
    }

    internal static string Key(string operation, JsonObject source, string field) =>
        "preview:" + (source["habit_id"] ?? source["goal_id"] ?? source["template_id"])?.ToString()
        + ":" + AgentOperationFingerprint.Compute(operation,
            (source["habit_id"] ?? source["goal_id"] ?? source["template_id"] ?? source["title"] ?? source["name"])?.ToString() ?? "draft")[..24]
        + ":" + field;

    internal static JsonObject? Source(JsonObject root, string operation, string itemId)
    {
        if (operation == "bulk_create_habits" && root["habits"] is JsonArray habits)
            return habits.OfType<JsonObject>().FirstOrDefault(item => item["preview_item_id"]?.ToString() == itemId)
                ?? (int.TryParse(itemId, out var i) && i >= 0 && i < habits.Count ? habits[i] as JsonObject : null);
        if (operation == "bulk_update_habits")
            return (root["revised_items"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(item => item["habit_id"]?.ToString() == itemId)?[ToolArgumentNames.Updates] as JsonObject
                ?? root[ToolArgumentNames.Updates] as JsonObject;
        return root;
    }

    internal static IReadOnlyList<string> RemovalFields(JsonObject suggestions, string field) =>
        ReminderFields.Contains(field, StringComparer.Ordinal)
            ? ReminderFields.Where(suggestions.ContainsKey).ToArray() : [field];

    private static readonly string[] ReminderFields = ["reminder_enabled", "reminder_times", "scheduled_reminders", "relative_reminders", "repeat_reminder"];

    internal static JsonObject? AtPath(JsonObject root, string path, out string field)
    {
        var segments = path.Split('/');
        field = segments[^1];
        JsonNode? node = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            node = PathChild(node, segments[index]);
            if (node is null) return null;
        }
        return node as JsonObject;
    }

    private static JsonNode? PathChild(JsonNode? node, string segment)
    {
        if (node is JsonObject obj) return obj[segment];
        if (node is JsonArray array && int.TryParse(segment, out var number) && number >= 0 && number < array.Count)
            return array[number];
        return null;
    }

    internal static bool AcceptsEdit(JsonElement schema, string path, JsonElement value)
    {
        var segments = path.Split('/');
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (schema.TryGetProperty(Properties, out var properties) && properties.TryGetProperty(segments[index], out var property))
                schema = property;
            else if (schema.TryGetProperty(ToolArgumentNames.Items, out var item) && int.TryParse(segments[index], out var number) && number >= 0)
                schema = item;
            else return false;
        }
        return AgentArgumentSchema.Accepts(schema, segments[^1], value);
    }

    internal static bool SetPath(JsonObject root, string path, JsonElement value)
    {
        var target = AtPath(root, path, out var field);
        if (target is null) return false;
        target[field] = JsonNode.Parse(value.GetRawText());
        if (target[Metadata]?["values"] is JsonObject suggestions && suggestions.ContainsKey(field))
            suggestions[field] = JsonNode.Parse(value.GetRawText());
        return true;
    }

    internal static bool Remove(JsonObject source, IReadOnlyList<string>? fields)
    {
        if (fields is null || fields.Count == 0) return true;
        var removals = new List<(JsonObject Node, string Field)>();
        foreach (var path in fields)
        {
            var node = AtPath(source, path, out var field);
            if (node?[Metadata]?["fields"] is not JsonObject suggestions || !suggestions.ContainsKey(field)) return false;
            removals.AddRange(RemovalFields(suggestions, field).Select(name => (node, name)));
        }
        foreach (var (node, field) in removals)
        {
            node.Remove(field);
            (node[Metadata]?["fields"] as JsonObject)?.Remove(field);
            (node[Metadata]?["values"] as JsonObject)?.Remove(field);
        }
        return true;
    }
}
