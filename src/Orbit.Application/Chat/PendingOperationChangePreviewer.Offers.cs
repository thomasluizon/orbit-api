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
            foreach (var habit in habits.OfType<JsonObject>())
                if (!await PrepareOfferNodeAsync(habit, itemSchema, userId, operation, client, ct)) return false;
            return true;
        }
        if (operation == "bulk_update_habits")
        {
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
        string operation, AgentClientContext client, CancellationToken ct, string? targetId = null)
    {
        if (source.Remove(ProactiveProposal.Argument, out var proposed))
        {
            if (proposed is not JsonObject suggestions || source.ContainsKey(ProactiveProposal.Metadata)) return false;
            var existing = await ExistingOfferValuesAsync(source, targetId, userId, ct);
            var accepted = new JsonObject();
            var keySource = (JsonObject)source.DeepClone();
            if (targetId is not null) keySource["habit_id"] = targetId;
            foreach (var suggestion in suggestions)
            {
                if (ExcludedSuggestion(suggestion.Key, source, suggestions, existing)) continue;
                var key = ProactiveProposal.Key(operation, keySource, suggestion.Key);
                if (source.ContainsKey(suggestion.Key) || IsExplicitlyCleared(source, suggestion.Key)
                    || client.DismissedOfferKeys?.Contains(key, StringComparer.Ordinal) == true) continue;
                if (!ProactiveProposal.Eligible(suggestion.Key) || suggestion.Value is null || !AgentArgumentSchema.Accepts(schema, suggestion.Key,
                    JsonSerializer.SerializeToElement(suggestion.Value))) return false;
                var value = suggestion.Value.DeepClone();
                value = MergeStoredSuggestion(suggestion.Key, value, existing.GetValueOrDefault(suggestion.Key));
                if (value is null) continue;
                accepted[suggestion.Key] = key;
                source[suggestion.Key] = value;
            }
            source[ProactiveProposal.Metadata] = new JsonObject
            {
                ["fields"] = accepted,
                ["conversation_id"] = client.ConversationId!.Value.ToString()
            };
        }
        if (source[ProactiveProposal.Metadata] is JsonObject metadata
            && (metadata["conversation_id"]?.ToString() != client.ConversationId?.ToString()
                || metadata["fields"] is not JsonObject)) return false;
        return true;
    }

    private static JsonNode? MergeStoredSuggestion(string field, JsonNode value, JsonNode? stored)
    {
        if (stored is null) return value;
        if (value is JsonArray added && stored is JsonArray previous)
            return new JsonArray(previous.Concat(added)
                .DistinctBy(item => field == "checklist_items" ? item?["text"]?.ToString().Trim().ToLowerInvariant() : item?.ToJsonString())
                .Select(item => item?.DeepClone()).ToArray());
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
        return [];
    }

    private static PendingOperationItem Annotate(PendingOperationItem item, JsonObject? source, string operation)
    {
        var suggestions = source?[ProactiveProposal.Metadata]?["fields"] as JsonObject;
        return item with
        {
            Fields = item.Fields.Where(field => field.Field != ProactiveProposal.Metadata).Select(field =>
                suggestions?[field.Field]?.ToString() is { } key
                    ? field with { Provenance = "suggested", IsEditable = true, Removal = new(key, field.Field) }
                    : field with { Provenance = field.OldValue == field.NewValue ? "stored" : "requested" }).ToArray()
        };
    }
}
