using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;
using Orbit.Domain.Common;
using Orbit.Application.Chat.Tools;

namespace Orbit.Application.Chat;

public static partial class ProactiveProposal
{
    public const string Argument = "suggested_fields";
    internal const string Metadata = "orbit_offer_metadata";
    private const string Properties = "properties";
    public const string Instruction = "For offer-enabled clients, put useful unrequested configuration in the ordinary operation fields and list their names in suggested_fields. List a name only after setting its non-null, non-empty value; never list unused options or null placeholders. Keep requested field names out of that list. Explicitly requested or prescribed clock times stay requested in scheduled_reminders, never in suggested_fields. Every unrequested emoji, schedule default or other added configuration belongs in suggested_fields; never present it as requested. Consider repeat reminders for activities repeated throughout the day, reminders, schedule and frequency, checklist steps, independently tracked children or a fitting owned parent, goal links, tags, emoji and a bounded end date. Only propose fields this operation supports and omit irrelevant options. Respect the account plan and capability availability; never propose locked features. Never replace requested or stored values, invent a daily target from an interval, or add end dates to OneTime or General items. Keep doctor-prescribed fixed times in scheduled_reminders; neither fixed medication times nor ordinary daily reading calls for a repeat reminder. Resolve existing parents, goals and tags from the supplied offer_account_facts first. To find a fitting parent, use its routine index as well as checking for duplicates of the new item; query only missing facts when that index is partial. When the message supplies a habit ID, pass it directly to the write tool; no query_habits lookup is needed. Put an unrequested parent on the item and list parent_habit_id in its suggested_fields; leave the bulk container parent unset. When a supplied active goal matches the requested activity, include its owned ID in goal_ids and list goal_ids in suggested_fields when unrequested; do not read those goals again. For a dated task with due_time, set reminder_enabled=true and non-empty reminder_times, listing both in suggested_fields when unrequested. Preserve requested or stored reminder configuration. Preserve stored checklist entries, tags and goal links when adding. A goal may need a target, unit, deadline or habit links; template reuse needs a grounded relationship and full contents before editing. Each suggestion stays editable and removable in the same approval preview. Nested drafts use their own suggested_fields; revise the exact slash-separated field path shown in the preview. Corrections preserve all other fields and never regenerate rejected suggestions. Do not add habit configuration to log, skip, delete, reorder, account reset, billing, API-key, support or administrator operations.";

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
        var root = JsonSerializer.SerializeToNode(schema)!.AsObject();
        if (!enabled)
            (root[Properties]?[ToolArgumentNames.Items]?[ToolArgumentNames.Items]?[Properties] as JsonObject)?.Remove("remove_suggested_fields");
        ChatToolControlOrder.Apply(root);
        return root;
    }

    public static object Schema(object schema) => BuildSchema(schema, inlineValues: false);

    /// <summary>Validates resolved offer values and pending drafts using the value-object representation.</summary>
    internal static object PreviewSchema(object schema) => BuildSchema(schema, inlineValues: true);

    private static object BuildSchema(object schema, bool inlineValues)
    {
        var root = JsonSerializer.SerializeToNode(schema)!.AsObject();
        AddSchema(root, inlineValues);
        return root;
    }

    private static void AddSchema(JsonObject schema, bool inlineValues)
    {
        if (schema[Properties] is not JsonObject properties) return;
        foreach (var name in new[] { "habits", "sub_habits" })
            if (properties[name]?[ToolArgumentNames.Items] is JsonObject item) AddSchema(item, inlineValues);
        if (properties[ToolArgumentNames.Updates] is JsonObject updates) AddSchema(updates, inlineValues);
        if (properties.ContainsKey("habits") || properties.ContainsKey(ToolArgumentNames.Updates)) return;
        var eligible = properties.Where(property => Fields.Contains(property.Key)).ToArray();
        if (eligible.Length == 0) return;
        if (!inlineValues)
            foreach (var property in eligible)
                if (property.Value is JsonObject field && FieldInstruction(property.Key) is { } instruction)
                    field["description"] = string.Join(" ", field["description"]?.ToString(), instruction).Trim();
        properties[Argument] = inlineValues ? new JsonObject
        {
            ["type"] = "object",
            ["description"] = Instruction,
            [Properties] = new JsonObject(eligible.Select(property => KeyValuePair.Create(property.Key, property.Value?.DeepClone()))),
            ["additionalProperties"] = false
        } : new JsonObject
        {
            ["type"] = "array",
            ["description"] = "Names of configured unrequested fields in suggested_fields. Set each ordinary field first; omit null or empty options. Include both reminder_enabled and reminder_times for an unrequested timed-task reminder, parent_habit_id for a fitting owned parent, and goal_ids for matching active goals. Use [] for none.",
            ["maxItems"] = eligible.Length,
            ["items"] = new JsonObject
            {
                ["type"] = "string"
            }
        };
        ChatToolControlOrder.Apply(schema);
    }

    internal static bool NormalizeSuggestions(JsonNode? node, JsonElement schema, out string? invalidField)
    {
        invalidField = null;
        if (node is JsonArray array)
        {
            var itemSchema = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("items", out var items) ? items : default;
            foreach (var item in array)
                if (!NormalizeSuggestions(item, itemSchema, out invalidField)) return false;
            return true;
        }
        if (node is not JsonObject source) return true;
        if (!NormalizeChildren(source, schema, out invalidField)) return false;
        if (!source.TryGetPropertyValue(Argument, out var proposed)) return true;
        if (proposed is null)
        {
            source.Remove(Argument);
            return true;
        }
        if (proposed is JsonObject values)
        {
            foreach (var field in values.ToArray())
                if (!SupportsSuggestion(schema, field.Key) || !HasValue(field.Value))
                {
                    values.Remove(field.Key);
                    if (!source.ContainsKey(field.Key)) source[field.Key] = field.Value;
                }
            return true;
        }
        if (proposed is not JsonArray names)
        {
            invalidField = Argument;
            return false;
        }
        return NormalizeNames(source, names, schema, out invalidField);
    }

    private static bool NormalizeChildren(JsonObject source, JsonElement schema, out string? invalidField)
    {
        invalidField = null;
        foreach (var property in source)
        {
            if (property.Key == Metadata) continue;
            if (property.Key == Argument && property.Value is JsonObject offered)
            {
                foreach (var field in offered)
                    if (!NormalizeSuggestions(field.Value, FieldSchema(schema, field.Key), out invalidField)) return false;
            }
            else if (property.Key != Argument && !NormalizeSuggestions(property.Value, FieldSchema(schema, property.Key), out invalidField)) return false;
        }
        return true;
    }

    private static bool NormalizeNames(JsonObject source, JsonArray names, JsonElement schema, out string? invalidField)
    {
        invalidField = null;
        var suggestions = new JsonObject();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (name is not JsonValue value || !value.TryGetValue<string>(out var field))
            {
                invalidField = name?.ToJsonString() ?? Argument;
                return false;
            }
            if (!SupportsSuggestion(schema, field) || !seen.Add(field) || !HasValue(source[field])) continue;
            source.Remove(field, out var configuration);
            suggestions[field] = configuration;
        }
        source[Argument] = suggestions;
        return true;
    }

    private static bool SupportsSuggestion(JsonElement schema, string field) =>
        Eligible(field) && FieldSchema(schema, field).ValueKind != JsonValueKind.Undefined;

    private static JsonElement FieldSchema(JsonElement schema, string field) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty(Properties, out var properties)
        && properties.TryGetProperty(field, out var value) ? value : default;

    internal static bool HasValue(JsonNode? value) => value is not null
        && value is not JsonArray { Count: 0 };

    public static bool Contains(JsonElement args) => ContainsNode(JsonNode.Parse(args.GetRawText()));
    private static bool ContainsNode(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(p => (p.Key is Argument or Metadata && !EmptyPlaceholder(p.Value)) || ContainsNode(p.Value)),
        JsonArray array => array.Any(ContainsNode),
        _ => false
    };

    private static bool EmptyPlaceholder(JsonNode? node) => node is null or JsonArray { Count: 0 } or JsonObject { Count: 0 };

    public static JsonElement OmitEmptySuggestions(JsonElement args)
    {
        var node = JsonNode.Parse(args.GetRawText());
        OmitEmptySuggestions(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void OmitEmptySuggestions(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue(Argument, out var value) && EmptyPlaceholder(value)) obj.Remove(Argument);
            foreach (var property in obj) OmitEmptySuggestions(property.Value);
        }
        else if (node is JsonArray array)
            foreach (var item in array) OmitEmptySuggestions(item);
    }

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
            obj.Remove(ProactiveOfferDismissal.Argument);
            obj.Remove(ProactiveReadOffer.Argument);
            obj.Remove(Metadata);
            obj.Remove(HabitPreviewDates.DefaultDate);
            if (suggestions) obj.Remove(Argument);
            else if (obj.TryGetPropertyValue(Argument, out var proposed) && EmptyPlaceholder(proposed)) obj.Remove(Argument);
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
