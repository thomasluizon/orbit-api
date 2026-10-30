using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat;

public sealed partial class PendingOperationChangePreviewer
{
    public async Task<Result<PendingOperationChangePreview?>> PreviewAsync(Guid userId, string operationId,
        JsonElement arguments, CancellationToken cancellationToken = default, AgentClientContext? clientContext = null)
    {
        if (operationId == AccountChangeSet.OperationName || !ProactiveProposal.Contains(arguments))
            return await PreviewCoreAsync(userId, operationId, arguments, cancellationToken, clientContext);
        if (!ProactiveProposal.Enabled(clientContext) || !ProactiveProposal.Supports(operationId))
            return Result.Failure<PendingOperationChangePreview?>("offers_not_supported");
        if (toolRegistry.GetTool(operationId) is not { } tool)
            return Result.Failure<PendingOperationChangePreview?>("preview_unavailable");
        var root = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        var prepared = await PrepareOfferNodesAsync(root, schema, userId, operationId, clientContext!, cancellationToken);
        if (!prepared) return Result.Failure<PendingOperationChangePreview?>("invalid_offer");
        var bound = JsonSerializer.SerializeToElement(root);
        var result = await PreviewCoreAsync(userId, operationId, bound, cancellationToken, clientContext);
        if (result.IsFailure || result.Value is not { Items: { } items } preview) return result;
        var effective = preview.ExecutionArguments ?? bound;
        root = JsonNode.Parse(effective.GetRawText())!.AsObject();
        var updated = items.Select(item => Annotate(item, ProactiveProposal.Source(root, operationId, item.ItemId), operationId)).ToArray();
        if (updated.Any(item => item.Fields.Any(field => field.Field.EndsWith("/repeat_reminder", StringComparison.Ordinal))))
        {
            var zone = await userDateService.GetUserTimeZoneAsync(userId, cancellationToken);
            updated = updated.Select(item => item with
            {
                Fields = item.Fields.Select(field =>
                field.Field.EndsWith("/repeat_reminder", StringComparison.Ordinal)
                    ? field with { NewValue = RepeatReminderPreview.Format(field.NewValue, clientContext, zone) } : field).ToArray()
            }).ToArray();
        }
        var changes = preview.Changes.Select(field => updated.SelectMany(item => item.Fields)
            .FirstOrDefault(candidate => candidate.EntityId == field.EntityId && candidate.Field == field.Field
                && candidate.EntityName == field.EntityName) ?? field).ToArray();
        return Result.Success<PendingOperationChangePreview?>(preview with
        {
            Items = updated,
            Changes = changes,
            ExecutionArguments = effective,
            PreviewFingerprint = AgentOperationFingerprint.Compute(operationId, JsonSerializer.Serialize(updated))
        });
    }

    private async Task<bool> PrepareOfferNodesAsync(JsonObject root, JsonElement schema, Guid userId,
        string operation, AgentClientContext client, CancellationToken ct)
    {
        if (operation == "bulk_create_habits" && root["habits"] is JsonArray habits)
        {
            var itemSchema = schema.GetProperty("properties").GetProperty("habits").GetProperty("items");
            if (root.ContainsKey(ProactiveProposal.Argument)) return false;
            var inherited = root["parent_habit_id"] is { } parent
                ? await ExistingOfferValuesAsync(new JsonObject { ["habit_id"] = parent.DeepClone() }, null, userId, ct) : null;
            foreach (var habit in habits.OfType<JsonObject>())
            {
                if (root.ContainsKey("parent_habit_id") && habit[ProactiveProposal.Argument] is JsonObject suggestions)
                    suggestions.Remove("parent_habit_id");
                if (!await PrepareOfferNodeAsync(habit, itemSchema, userId, operation, client, ct, inherited: inherited)) return false;
            }
            return true;
        }
        if (operation == "bulk_update_habits")
        {
            if (root.ContainsKey(ProactiveProposal.Argument)) return false;
            var itemSchema = schema.GetProperty("properties").GetProperty("updates");
            if (root["revised_items"] is not JsonArray items && root["updates"] is JsonObject updates
                && updates.ContainsKey(ProactiveProposal.Argument))
            {
                var (filter, error) = BulkHabitToolArguments.ParseRequiredFilter(JsonSerializer.SerializeToElement(root));
                if (error is not null || filter is null) return false;
                var selected = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter, ct);
                items = new JsonArray(selected.Select(habit => (JsonNode)new JsonObject
                { ["habit_id"] = habit.Id.ToString(), ["updates"] = updates.DeepClone() }).ToArray());
                root["revised_items"] = items;
                updates.Remove(ProactiveProposal.Argument);
            }
            if (root["revised_items"] is JsonArray revised)
                foreach (var item in revised.OfType<JsonObject>())
                {
                    if (item["updates"] is not JsonObject values) return false;
                    if (!await PrepareOfferNodeAsync(values, itemSchema, userId, operation, client, ct,
                        item["habit_id"]?.ToString())) return false;
                }
            return true;
        }
        return await PrepareOfferNodeAsync(root, schema, userId, operation, client, ct);
    }

    private async Task<bool> PrepareOfferNodeAsync(JsonObject source, JsonElement schema, Guid userId,
        string operation, AgentClientContext client, CancellationToken ct, string? targetId = null, string path = "",
        Dictionary<string, JsonNode?>? inherited = null)
    {
        var existing = await ExistingOfferValuesAsync(source, targetId, userId, ct);
        if (inherited is null && source["parent_habit_id"] is { } parent)
            inherited = await ExistingOfferValuesAsync(new JsonObject { ["habit_id"] = parent.DeepClone() }, null, userId, ct);
        if (source.Remove(ProactiveProposal.Argument, out var proposed))
        {
            if (proposed is not JsonObject suggestions || source.ContainsKey(ProactiveProposal.Metadata)) return false;
            var schedule = OfferSchedule(source, suggestions, existing, inherited);
            var accepted = new JsonObject();
            var values = new JsonObject();
            var proposalSchema = JsonSerializer.SerializeToElement(ProactiveProposal.Schema(schema));
            var keySource = (JsonObject)source.DeepClone();
            if (targetId is not null) keySource["habit_id"] = targetId;
            foreach (var suggestion in suggestions)
            {
                if (suggestion.Value is null || ExcludedSuggestion(suggestion.Key, source, suggestions, schedule)) continue;
                var key = ProactiveProposal.Key(operation, keySource, path + suggestion.Key);
                if (source.ContainsKey(suggestion.Key) || IsExplicitlyCleared(source, suggestion.Key)
                    || client.DismissedOfferKeys?.Contains(key, StringComparer.Ordinal) == true) continue;
                if (!ProactiveProposal.Eligible(suggestion.Key) || !AgentArgumentSchema.Accepts(proposalSchema, suggestion.Key,
                    JsonSerializer.SerializeToElement(suggestion.Value))) return false;
                var value = suggestion.Value.DeepClone();
                value = MergeStoredSuggestion(suggestion.Key, value, existing.GetValueOrDefault(suggestion.Key));
                if (value is null) continue;
                accepted[suggestion.Key] = key;
                values[suggestion.Key] = suggestion.Value.DeepClone();
                source[suggestion.Key] = value;
            }
            source[ProactiveProposal.Metadata] = new JsonObject
            {
                ["fields"] = accepted,
                ["values"] = values,
                ["conversation_id"] = client.ConversationId!.Value.ToString()
            };
        }
        if (!await RefreshOfferNodeAsync(source, targetId, userId, client, ct)) return false;
        return await PrepareNestedOfferNodesAsync(source, schema, userId, operation, client, ct, path,
            OfferSchedule(source, new JsonObject(), existing, inherited));
    }

    private static Dictionary<string, JsonNode?> OfferSchedule(JsonObject source, JsonObject suggestions,
        Dictionary<string, JsonNode?> stored, Dictionary<string, JsonNode?>? inherited)
    {
        var result = new Dictionary<string, JsonNode?>(stored);
        foreach (var field in new[] { "is_general", "is_flexible", "frequency_unit", "frequency_quantity", "interval_weeks", "days" })
            result[field] = source[field] ?? suggestions[field] ?? stored.GetValueOrDefault(field) ?? inherited?.GetValueOrDefault(field);
        if (source["frequency_unit"] is not null && source["frequency_quantity"] is null && suggestions["frequency_quantity"] is null)
            result["frequency_quantity"] = JsonValue.Create(1);
        if (inherited is not null && source["due_date"] is not null && source["start_date"] is null && source["days"] is null)
            result["days"] = new JsonArray();
        return result;
    }

    private async Task<bool> PrepareNestedOfferNodesAsync(JsonObject source, JsonElement schema, Guid userId,
        string operation, AgentClientContext client, CancellationToken ct, string path, Dictionary<string, JsonNode?> schedule)
    {
        if (source["sub_habits"] is JsonArray children
            && schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("sub_habits", out var childSchema)
            && childSchema.TryGetProperty("items", out var itemSchema))
            for (var index = 0; index < children.Count; index++)
            {
                if (children[index] is not JsonObject child) return false;
                if (child[ProactiveProposal.Argument] is JsonObject suggestions) suggestions.Remove("parent_habit_id");
                if (!await PrepareOfferNodeAsync(child, itemSchema, userId, operation, client, ct,
                    path: path + "sub_habits/" + index + "/", inherited: schedule)) return false;
            }
        return true;
    }

    private async Task<bool> RefreshOfferNodeAsync(JsonObject source, string? targetId, Guid userId, AgentClientContext client, CancellationToken ct)
    {
        if (source[ProactiveProposal.Metadata] is not JsonObject metadata) return true;
        if (metadata["conversation_id"]?.ToString() != client.ConversationId?.ToString()
            || metadata["fields"] is not JsonObject fields) return false;
        if (metadata["values"] is not JsonObject values) return true;
        var stored = await ExistingOfferValuesAsync(source, targetId, userId, ct);
        foreach (var field in fields.ToArray())
        {
            if (values[field.Key] is not { } proposed || !stored.TryGetValue(field.Key, out var previous)) continue;
            var merged = MergeStoredSuggestion(field.Key, proposed.DeepClone(), previous);
            if (merged is null)
            {
                source.Remove(field.Key);
                fields.Remove(field.Key);
                values.Remove(field.Key);
            }
            else source[field.Key] = merged;
        }
        return true;
    }

    private static JsonNode? MergeStoredSuggestion(string field, JsonNode value, JsonNode? stored)
    {
        if (stored is null) return value;
        if (value is JsonArray added && stored is JsonArray previous)
        {
            string Key(JsonNode? item) => field == "checklist_items" ? item?["text"]?.ToString().Trim().ToLowerInvariant() ?? ""
                : field is "tag_names" or "items" ? item?.ToString().Trim().ToLowerInvariant() ?? "" : item?.ToJsonString() ?? "null";
            var merged = new JsonArray(previous.Select(item => item?.DeepClone()).ToArray());
            var seen = previous.Select(Key).ToHashSet(StringComparer.Ordinal);
            foreach (var item in added)
                if (seen.Add(Key(item))) merged.Add(item?.DeepClone());
            return merged;
        }
        return stored.ToString() is "" or "false" or "null" or "[]" ? value : null;
    }

    private static bool HasFixedReminders(JsonObject source, Dictionary<string, JsonNode?> stored) =>
        new[] { "scheduled_reminders", "relative_reminders", "reminder_times" }.Any(field => source.ContainsKey(field)
            || !string.IsNullOrEmpty(stored.GetValueOrDefault(field)?.ToString()));

    private static bool ExcludedSuggestion(string field, JsonObject source, JsonObject suggestions,
        Dictionary<string, JsonNode?> stored)
    {
        JsonNode? Value(string name) => source[name] ?? suggestions[name] ?? stored.GetValueOrDefault(name);
        if (field == "end_date") return Value("is_general")?.ToString() == "true"
            || Value("frequency_unit") is null || Value("frequency_unit")?.ToString() == "OneTime";
        if (field != "repeat_reminder") return false;
        if (HasFixedReminders(source, stored)) return true;
        var schedule = Value("is_general")?.ToString() == "true" ? HabitSchedule.General
            : Value("is_flexible")?.ToString() == "true" ? HabitSchedule.Flexible : HabitSchedule.Recurring;
        var unit = Enum.TryParse<FrequencyUnit>(Value("frequency_unit")?.ToString(), out var parsed) ? parsed : (FrequencyUnit?)null;
        var quantity = int.TryParse(Value("frequency_quantity")?.ToString(), out var count) ? count : (int?)null;
        var interval = int.TryParse(Value("interval_weeks")?.ToString(), out var weeks) ? weeks : (int?)null;
        var days = (Value("days") as JsonArray)?.Select(day => Enum.TryParse<DayOfWeek>(day?.ToString(), out var weekday) ? weekday : (DayOfWeek)(-1));
        return !RepeatReminderOptions.SupportsSchedule(schedule, unit, quantity, days, interval);
    }

    private static bool IsExplicitlyCleared(JsonObject source, string field) =>
        (source["clear_fields"] as JsonArray)?.Any(value => value?.ToString() == field) == true
        || field == "parent_habit_id" && source["clear_parent"]?.ToString() == "true";

    private async Task<Dictionary<string, JsonNode?>> ExistingOfferValuesAsync(JsonObject source, string? targetId,
        Guid userId, CancellationToken ct)
    {
        if (Guid.TryParse(targetId ?? source["habit_id"]?.ToString(), out var habitId))
        {
            var habits = await habitRepository.FindAsync(habit => habit.Id == habitId && habit.UserId == userId && !habit.IsDeleted,
                query => query.Include(habit => habit.Goals).Include(habit => habit.Tags), ct);
            if (habits.SingleOrDefault() is { } habit)
            {
                var values = HabitValues(habit).ToDictionary(pair => pair.Key,
                    pair => (JsonNode?)JsonValue.Create(pair.Value));
                values["is_general"] = JsonValue.Create(habit.Schedule == HabitSchedule.General);
                values["is_flexible"] = JsonValue.Create(habit.Schedule == HabitSchedule.Flexible);
                values["frequency_unit"] = JsonValue.Create(habit.FrequencyUnit?.ToString());
                values["frequency_quantity"] = JsonValue.Create(habit.FrequencyQuantity);
                values["interval_weeks"] = JsonValue.Create(habit.IntervalWeeks);
                values["days"] = JsonSerializer.SerializeToNode(habit.Days.Select(day => day.ToString()));
                values["tag_ids"] = JsonSerializer.SerializeToNode(habit.Tags.Select(tag => tag.Id));
                values["tag_names"] = JsonSerializer.SerializeToNode(habit.Tags.Select(tag => tag.Name));
                values["goal_ids"] = JsonSerializer.SerializeToNode(habit.Goals.Select(goal => goal.Id));
                values["checklist_items"] = JsonSerializer.SerializeToNode(habit.ChecklistItems.Select(item => new { text = item.Text, is_checked = item.IsChecked }));
                values["parent_habit_id"] = JsonSerializer.SerializeToNode(habit.ParentHabitId);
                return values;
            }
        }
        if (Guid.TryParse(source["goal_id"]?.ToString(), out var goalId))
        {
            var goals = await goalRepository.FindAsync(goal => goal.Id == goalId && goal.UserId == userId && !goal.IsDeleted, ct);
            if (goals.SingleOrDefault() is { } goal)
                return new()
                {
                    ["description"] = JsonValue.Create(goal.Description),
                    ["deadline"] = JsonSerializer.SerializeToNode(goal.Deadline),
                    ["unit"] = JsonValue.Create(goal.Unit),
                    ["target_value"] = JsonValue.Create(goal.TargetValue)
                };
        }
        if (Guid.TryParse(source["template_id"]?.ToString(), out var templateId))
        {
            if (templateRepository is null) return new() { ["items"] = new JsonObject() };
            var templates = await templateRepository.FindAsync(template => template.Id == templateId && template.UserId == userId && !template.IsDeleted, ct);
            if (templates.SingleOrDefault() is { } template)
                return new() { ["items"] = JsonSerializer.SerializeToNode(template.Items) };
        }
        return [];
    }

    private static PendingOperationItem Annotate(PendingOperationItem item, JsonObject? source, string operation)
    {
        var suggestions = source?[ProactiveProposal.Metadata]?["fields"] as JsonObject;
        var fields = item.Fields.Where(field => field.Field != ProactiveProposal.Metadata).Select(field =>
            suggestions?[field.Field]?.ToString() is { } key
                ? field with { Provenance = "suggested", IsEditable = true, Removal = new(key, field.Field, ProactiveProposal.RemovalFields(suggestions!, field.Field)) }
                : field with { Provenance = source?.ContainsKey(field.Field) == true ? "requested" : "stored" }).ToList();
        if (source is not null) AddNestedOfferFields(fields, source, "");
        return item with { Fields = fields };
    }

    private static void AddNestedOfferFields(List<PendingOperationChange> fields, JsonObject source, string prefix)
    {
        if (source["sub_habits"] is not JsonArray children) return;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not JsonObject child) continue;
            var path = prefix + "sub_habits/" + index + "/";
            if (child[ProactiveProposal.Metadata]?["fields"] is JsonObject suggestions)
                foreach (var suggestion in suggestions)
                {
                    if (child[suggestion.Key] is not { } proposed) continue;
                    var clean = ProactiveProposal.StripMetadata(JsonSerializer.SerializeToElement(proposed));
                    var id = Guid.TryParse(child["habit_id"]?.ToString(), out var entity) ? entity : Guid.Empty;
                    fields.Add(new(id, child["title"]?.ToString() ?? "", path + suggestion.Key, null,
                        AgentArgumentPreview.FormatValue(clean), AgentArgumentPreview.ResolveValueType(suggestion.Key, clean), clean,
                        IsEditable: true, EntityEmoji: child["emoji"]?.ToString(), Provenance: "suggested",
                        Removal: new(suggestion.Value!.ToString(), path + suggestion.Key,
                            ProactiveProposal.RemovalFields(suggestions, suggestion.Key).Select(field => path + field).ToArray())));
                }
            AddNestedOfferFields(fields, child, path);
        }
    }
}
