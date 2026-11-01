using static Orbit.Application.Chat.Tools.ToolArgumentNames;
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

namespace Orbit.Application.Chat;

public sealed partial class PendingOperationChangePreviewer
{
    public async Task<Result<PendingOperationChangePreview?>> PreviewAsync(Guid userId, string operationId,
        JsonElement arguments, CancellationToken cancellationToken = default, AgentClientContext? clientContext = null)
    {
        if (operationId is "create_habit" or "bulk_create_habits")
        {
            var node = JsonNode.Parse(arguments.GetRawText())!.AsObject();
            var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
            var sources = node["habits"] is JsonArray habits ? habits.OfType<JsonObject>().ToArray() : [node];
            foreach (var source in sources)
                if (node["parent_habit_id"] is null || source["frequency_unit"]?.ToString() == "OneTime")
                    HabitPreviewDates.Bind(source, today);
            arguments = JsonSerializer.SerializeToElement(node);
        }
        var result = await PreviewWithOffersAsync(userId, operationId, arguments, cancellationToken, clientContext);
        if (result.IsFailure || result.Value is not { } preview) return result;
        if (operationId is not ("create_habit" or "create_sub_habit" or "bulk_create_habits"
            or "update_habit" or "bulk_update_habits" or "bulk_reschedule_habits")) return result;
        PendingOperationChange DateField(PendingOperationChange field, JsonObject? source) => field.Field is "due_date" or "due_time"
            ? field with
            {
                IsEditable = field.IsEditable || field.Field == "due_date"
                    && source?["frequency_unit"]?.ToString() == "OneTime",
                Provenance = field.Field == "due_date" && source?[HabitPreviewDates.DefaultDate]?.ToString() == "true"
                    ? "suggested" : field.Provenance ?? "requested"
            } : field;
        var root = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        var updated = preview.Items?.Select(item => item with
        {
            Fields = item.Fields.Select(field => DateField(field, ProactiveProposal.Source(root, operationId, item.ItemId))).ToArray()
        }).ToArray();
        return Result.Success<PendingOperationChangePreview?>(preview with
        {
            Items = updated,
            Changes = preview.Changes.Select(field => DateField(field, root)).ToArray(),
            PreviewFingerprint = updated is null ? preview.PreviewFingerprint
                : AgentOperationFingerprint.Compute(operationId, JsonSerializer.Serialize(updated))
        });
    }

    private async Task<Result<PendingOperationChangePreview?>> PreviewWithOffersAsync(Guid userId, string operationId,
        JsonElement arguments, CancellationToken cancellationToken, AgentClientContext? clientContext)
    {
        arguments = ProactiveProposal.OmitEmptySuggestions(arguments);
        var offersEnabled = ProactiveProposal.Enabled(clientContext) && ProactiveProposal.Supports(operationId);
        if (operationId == AccountChangeSet.OperationName || (!ProactiveProposal.Contains(arguments) && !offersEnabled))
            return await PreviewCoreAsync(userId, operationId, arguments, clientContext, cancellationToken);
        if (!offersEnabled)
            return Result.Failure<PendingOperationChangePreview?>("offers_not_supported");
        if (toolRegistry.GetTool(operationId) is not { } tool)
            return Result.Failure<PendingOperationChangePreview?>("preview_unavailable");
        var root = JsonNode.Parse(OptionalToolArguments.OmitOptionalNulls(arguments, tool.GetParameterSchema()).GetRawText())!.AsObject();
        if (!ProactiveProposal.NormalizeSuggestions(root, out var invalidField))
            return Result.Failure<PendingOperationChangePreview?>($"Invalid suggested_fields entry {invalidField}: use a supported field name with a value.", "invalid_offer");
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());
        var preparation = new OfferPreparation(userId, operationId, clientContext!);
        var prepared = await PrepareOfferNodesAsync(root, schema, preparation, cancellationToken);
        if (!prepared) return Result.Failure<PendingOperationChangePreview?>(
            $"Invalid suggested_fields entry {preparation.InvalidField ?? ProactiveProposal.Argument}: check its value against this operation schema.", "invalid_offer");
        var bound = JsonSerializer.SerializeToElement(root);
        var result = await PreviewCoreAsync(userId, operationId, bound, clientContext, cancellationToken);
        if (result.IsFailure || result.Value is not { Items: { } items } preview) return result;
        var effective = preview.ExecutionArguments ?? bound;
        root = JsonNode.Parse(effective.GetRawText())!.AsObject();
        var updated = items.Select(item => Annotate(item, ProactiveProposal.Source(root, operationId, item.ItemId))).ToArray();
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

    private sealed record OfferPreparation(Guid UserId, string Operation, AgentClientContext Client)
    {
        public string? InvalidField { get; set; }
    }
    private sealed record OfferNodeContext(string? TargetId = null, string Path = "",
        Dictionary<string, JsonNode?>? Inherited = null);

    private Task<bool> PrepareOfferNodesAsync(JsonObject root, JsonElement schema,
        OfferPreparation preparation, CancellationToken ct) => preparation.Operation switch
        {
            BulkCreateHabits when root[ToolArgumentNames.Habits] is JsonArray habits => PrepareBulkCreateOfferNodesAsync(root, habits, schema, preparation, ct),
            "bulk_update_habits" => PrepareBulkUpdateOfferNodesAsync(root, schema, preparation, ct),
            _ => PrepareOfferNodeAsync(root, schema, preparation, new(), ct)
        };

    private async Task<bool> PrepareBulkCreateOfferNodesAsync(JsonObject root, JsonArray habits,
        JsonElement schema, OfferPreparation preparation, CancellationToken ct)
    {
        var itemSchema = schema.GetProperty("properties").GetProperty(ToolArgumentNames.Habits).GetProperty(Items);
        if (root.ContainsKey(ProactiveProposal.Argument)) return false;
        var inherited = root[ParentHabitId] is { } parent
            ? await ExistingOfferValuesAsync(new JsonObject { [HabitId] = parent.DeepClone() }, null, preparation.UserId, ct) : null;
        foreach (var habit in habits.OfType<JsonObject>())
        {
            if (ProactiveProposal.HasValue(root[ParentHabitId]) && habit[ProactiveProposal.Argument] is JsonObject suggestions)
                suggestions.Remove(ParentHabitId);
            if (!await PrepareOfferNodeAsync(habit, itemSchema, preparation, new(Inherited: inherited), ct)) return false;
        }
        return true;
    }

    private async Task<bool> PrepareBulkUpdateOfferNodesAsync(JsonObject root, JsonElement schema,
        OfferPreparation preparation, CancellationToken ct)
    {
        if (root.ContainsKey(ProactiveProposal.Argument)) return false;
        var itemSchema = schema.GetProperty("properties").GetProperty(Updates);
        if (!await ExpandBulkOfferUpdatesAsync(root, preparation.UserId, ct)) return false;
        if (root["revised_items"] is JsonArray revised)
            foreach (var item in revised.OfType<JsonObject>())
            {
                if (item[Updates] is not JsonObject values) return false;
                if (!await PrepareOfferNodeAsync(values, itemSchema, preparation, new(item[HabitId]?.ToString()), ct)) return false;
            }
        return true;
    }

    private async Task<bool> ExpandBulkOfferUpdatesAsync(JsonObject root, Guid userId, CancellationToken ct)
    {
        if (root["revised_items"] is JsonArray || root[Updates] is not JsonObject updates
            || !updates.ContainsKey(ProactiveProposal.Argument)) return true;
        var (filter, error) = BulkHabitToolArguments.ParseRequiredFilter(JsonSerializer.SerializeToElement(root));
        if (error is not null || filter is null) return false;
        var selected = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter, ct);
        root["revised_items"] = new JsonArray(selected.Select(habit => (JsonNode)new JsonObject
        { [HabitId] = habit.Id.ToString(), [Updates] = updates.DeepClone() }).ToArray());
        updates.Remove(ProactiveProposal.Argument);
        return true;
    }

    private async Task<bool> PrepareOfferNodeAsync(JsonObject source, JsonElement schema,
        OfferPreparation preparation, OfferNodeContext node, CancellationToken ct)
    {
        var existing = await ExistingOfferValuesAsync(source, node.TargetId, preparation.UserId, ct);
        var inherited = node.Inherited;
        if (inherited is null && source[ParentHabitId] is { } parent)
            inherited = await ExistingOfferValuesAsync(new JsonObject { [HabitId] = parent.DeepClone() }, null, preparation.UserId, ct);
        if (!ApplyOfferSuggestions(source, schema, preparation, node, existing, inherited)) return false;
        if (!await RefreshOfferNodeAsync(source, node.TargetId, preparation.UserId, preparation.Client, ct)) return false;
        return await PrepareNestedOfferNodesAsync(source, schema, preparation,
            node with { Inherited = OfferSchedule(source, new JsonObject(), existing, inherited) }, ct);
    }

    private static bool ApplyOfferSuggestions(JsonObject source, JsonElement schema, OfferPreparation preparation,
        OfferNodeContext node, Dictionary<string, JsonNode?> existing, Dictionary<string, JsonNode?>? inherited)
    {
        if (!source.Remove(ProactiveProposal.Argument, out var proposed) && source.ContainsKey(ProactiveProposal.Metadata)) return true;
        if (proposed is not null and not JsonObject || source.ContainsKey(ProactiveProposal.Metadata)) return false;
        var suggestions = proposed as JsonObject ?? new JsonObject();
        var requested = new JsonArray(source.Where(field => ProactiveProposal.HasValue(field.Value))
            .Select(field => (JsonNode?)JsonValue.Create(field.Key)).ToArray());
        var requestedValues = (JsonObject)source.DeepClone();
        if (ProactiveProposal.HasValue(suggestions["repeat_reminder"])
            && FixedReminderFields.Any(field => ProactiveProposal.HasValue(suggestions[field])))
        {
            preparation.InvalidField = "repeat_reminder";
            return false;
        }
        var schedule = OfferSchedule(source, suggestions, existing, inherited);
        var accepted = new JsonObject();
        var values = new JsonObject();
        var proposalSchema = JsonSerializer.SerializeToElement(ProactiveProposal.PreviewSchema(schema));
        var keySource = (JsonObject)source.DeepClone();
        if (node.TargetId is not null) keySource[HabitId] = node.TargetId;
        foreach (var suggestion in suggestions)
        {
            if (suggestion.Value is null || ExcludedSuggestion(suggestion.Key, requestedValues, suggestions, schedule)) continue;
            var key = ProactiveProposal.Key(preparation.Operation, keySource, node.Path + suggestion.Key);
            if (IgnoreOfferSuggestion(source, preparation.Client, suggestion.Key, key)) continue;
            if (!ProactiveProposal.Eligible(suggestion.Key) || !AgentArgumentSchema.Accepts(proposalSchema, suggestion.Key,
                JsonSerializer.SerializeToElement(suggestion.Value)))
            {
                preparation.InvalidField = suggestion.Key;
                return false;
            }
            var value = MergeStoredSuggestion(suggestion.Key, suggestion.Value.DeepClone(), existing.GetValueOrDefault(suggestion.Key));
            if (value is null) continue;
            accepted[suggestion.Key] = key;
            values[suggestion.Key] = suggestion.Value.DeepClone();
            source[suggestion.Key] = value;
        }
        source[ProactiveProposal.Metadata] = new JsonObject
        {
            ["requested_fields"] = requested,
            ["fields"] = accepted,
            ["values"] = values,
            ["conversation_id"] = preparation.Client.ConversationId!.Value.ToString()
        };
        return true;
    }

    private static bool IgnoreOfferSuggestion(JsonObject source, AgentClientContext client, string field, string key) =>
        ProactiveProposal.HasValue(source[field]) || IsExplicitlyCleared(source, field)
        || client.DismissedOfferKeys?.Contains(key, StringComparer.Ordinal) == true;

    private static Dictionary<string, JsonNode?> OfferSchedule(JsonObject source, JsonObject suggestions,
        Dictionary<string, JsonNode?> stored, Dictionary<string, JsonNode?>? inherited)
    {
        var result = new Dictionary<string, JsonNode?>(stored);
        foreach (var field in OfferScheduleFields)
            result[field] = source[field] ?? suggestions[field] ?? stored.GetValueOrDefault(field) ?? inherited?.GetValueOrDefault(field);
        if (source[FrequencyUnitField] is not null && source[FrequencyQuantityField] is null && suggestions[FrequencyQuantityField] is null)
            result[FrequencyQuantityField] = JsonValue.Create(1);
        if (inherited is not null && source["due_date"] is not null && source["start_date"] is null && source["days"] is null)
            result["days"] = new JsonArray();
        return result;
    }

    private async Task<bool> PrepareNestedOfferNodesAsync(JsonObject source, JsonElement schema,
        OfferPreparation preparation, OfferNodeContext node, CancellationToken ct)
    {
        if (source[SubHabits] is not JsonArray children
            || !schema.TryGetProperty("properties", out var properties) || !properties.TryGetProperty(SubHabits, out var childSchema)
            || !childSchema.TryGetProperty(Items, out var itemSchema)) return true;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not JsonObject child) return false;
            if (child[ProactiveProposal.Argument] is JsonObject suggestions) suggestions.Remove(ParentHabitId);
            if (!await PrepareOfferNodeAsync(child, itemSchema, preparation,
                new(Path: NestedOfferPath(node.Path, index), Inherited: node.Inherited), ct)) return false;
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
        foreach (var field in fields.Select(field => field.Key).ToArray())
        {
            if (values[field] is not { } proposed || !stored.TryGetValue(field, out var previous)) continue;
            var merged = MergeStoredSuggestion(field, proposed.DeepClone(), previous);
            if (merged is null)
            {
                source.Remove(field);
                fields.Remove(field);
                values.Remove(field);
            }
            else source[field] = merged;
        }
        return true;
    }

    private static JsonNode? MergeStoredSuggestion(string field, JsonNode value, JsonNode? stored)
    {
        if (stored is null) return value;
        if (value is JsonArray added && stored is JsonArray previous)
        {
            var merged = new JsonArray(previous.Select(item => item?.DeepClone()).ToArray());
            var seen = previous.Select(item => StoredSuggestionKey(field, item)).ToHashSet(StringComparer.Ordinal);
            foreach (var item in added.Where(item => seen.Add(StoredSuggestionKey(field, item))))
                merged.Add(item?.DeepClone());
            return merged;
        }
        return stored.ToString() is "" or "false" or "null" or "[]" ? value : null;
    }

    private static string StoredSuggestionKey(string field, JsonNode? item) => field switch
    {
        ChecklistItemsField => item?["text"]?.ToString().Trim().ToLowerInvariant() ?? "",
        TagNames or Items => item?.ToString().Trim().ToLowerInvariant() ?? "",
        _ => item?.ToJsonString() ?? "null"
    };

    private static readonly string[] FixedReminderFields = [ScheduledRemindersField, RelativeRemindersField, ReminderTimesField];
    private static readonly string[] OfferScheduleFields = [IsGeneralField, IsFlexible, FrequencyUnitField, FrequencyQuantityField, IntervalWeeks, "days"];

    private static bool HasFixedReminders(JsonObject source, Dictionary<string, JsonNode?> stored) =>
        FixedReminderFields.Any(field => ProactiveProposal.HasValue(source[field])
            || stored.GetValueOrDefault(field)?.ToString() is { Length: > 0 } value && value != "[]");

    private static bool ExcludedSuggestion(string field, JsonObject source, JsonObject suggestions,
        Dictionary<string, JsonNode?> stored)
    {
        JsonNode? Value(string name) => source[name] ?? suggestions[name] ?? stored.GetValueOrDefault(name);
        if (field == "end_date") return Value(IsGeneralField)?.ToString() == "true"
            || Value(FrequencyUnitField) is null || Value(FrequencyUnitField)?.ToString() == "OneTime";
        return field == "repeat_reminder" && HasFixedReminders(source, stored);
    }

    private static bool IsExplicitlyCleared(JsonObject source, string field) =>
        (source["clear_fields"] as JsonArray)?.Any(value => value?.ToString() == field) == true
        || field == ParentHabitId && source["clear_parent"]?.ToString() == "true";

    private async Task<Dictionary<string, JsonNode?>> ExistingOfferValuesAsync(JsonObject source, string? targetId,
        Guid userId, CancellationToken ct)
    {
        if (Guid.TryParse(targetId ?? source[HabitId]?.ToString(), out var habitId))
        {
            var habits = await habitRepository.FindAsync(habit => habit.Id == habitId && habit.UserId == userId && !habit.IsDeleted,
                query => query.Include(habit => habit.Goals).Include(habit => habit.Tags).AsSplitQuery(), ct);
            if (habits.SingleOrDefault() is { } habit)
            {
                var values = HabitValues(habit).ToDictionary(pair => pair.Key,
                    pair => (JsonNode?)JsonValue.Create(pair.Value));
                values[IsGeneralField] = JsonValue.Create(habit.Schedule == HabitSchedule.General);
                values["is_flexible"] = JsonValue.Create(habit.Schedule == HabitSchedule.Flexible);
                values[FrequencyUnitField] = JsonValue.Create(habit.FrequencyUnit?.ToString());
                values[FrequencyQuantityField] = JsonValue.Create(habit.FrequencyQuantity);
                values["interval_weeks"] = JsonValue.Create(habit.IntervalWeeks);
                values["days"] = JsonSerializer.SerializeToNode(habit.Days.Select(day => day.ToString()));
                values["tag_ids"] = JsonSerializer.SerializeToNode(habit.Tags.Select(tag => tag.Id));
                values["tag_names"] = JsonSerializer.SerializeToNode(habit.Tags.Select(tag => tag.Name));
                values["goal_ids"] = JsonSerializer.SerializeToNode(habit.Goals.Select(goal => goal.Id));
                values["checklist_items"] = JsonSerializer.SerializeToNode(habit.ChecklistItems.Select(item => new { text = item.Text, is_checked = item.IsChecked }));
                values[ParentHabitId] = JsonSerializer.SerializeToNode(habit.ParentHabitId);
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
            if (templateRepository is null) return new() { [Items] = new JsonObject() };
            var templates = await templateRepository.FindAsync(template => template.Id == templateId && template.UserId == userId && !template.IsDeleted, ct);
            if (templates.SingleOrDefault() is { } template)
                return new() { [Items] = JsonSerializer.SerializeToNode(template.Items) };
        }
        return [];
    }

    private static PendingOperationItem Annotate(PendingOperationItem item, JsonObject? source)
    {
        var suggestions = source?[ProactiveProposal.Metadata]?["fields"] as JsonObject;
        var fields = item.Fields.Where(field => field.Field != ProactiveProposal.Metadata)
            .Select(field => AnnotateOfferField(field, source, suggestions)).ToList();
        if (source is not null) AddNestedOfferFields(fields, source, "");
        return item with { Fields = fields };
    }

    private static PendingOperationChange AnnotateOfferField(PendingOperationChange field, JsonObject? source, JsonObject? suggestions)
    {
        if (suggestions?[field.Field]?.ToString() is { } key)
            return field with { Provenance = "suggested", IsEditable = true, Removal = new(key, field.Field, ProactiveProposal.RemovalFields(suggestions, field.Field)) };
        var requested = source?[ProactiveProposal.Metadata]?["requested_fields"] as JsonArray;
        var supplied = requested is not null ? requested.Any(name => name?.ToString() == field.Field)
            : ProactiveProposal.HasValue(source?[field.Field]);
        return field with { Provenance = supplied ? "requested" : "stored" };
    }

    private static void AddNestedOfferFields(List<PendingOperationChange> fields, JsonObject source, string prefix)
    {
        if (source[SubHabits] is not JsonArray children) return;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not JsonObject child) continue;
            var path = NestedOfferPath(prefix, index);
            AddChildOfferFields(fields, child, path);
            AddNestedOfferFields(fields, child, path);
        }
    }

    private static string NestedOfferPath(string prefix, int index) => $"{prefix}{SubHabits}/{index}/";

    private static void AddChildOfferFields(List<PendingOperationChange> fields, JsonObject child, string path)
    {
        if (child[ProactiveProposal.Metadata]?["fields"] is not JsonObject suggestions) return;
        foreach (var suggestion in suggestions)
        {
            if (child[suggestion.Key] is not { } proposed) continue;
            var clean = ProactiveProposal.StripMetadata(JsonSerializer.SerializeToElement(proposed));
            var id = Guid.TryParse(child[HabitId]?.ToString(), out var entity) ? entity : Guid.Empty;
            fields.Add(new(id, child["title"]?.ToString() ?? "", path + suggestion.Key, null,
                AgentArgumentPreview.FormatValue(clean), AgentArgumentPreview.ResolveValueType(suggestion.Key, clean), clean,
                IsEditable: true, EntityEmoji: child["emoji"]?.ToString(), Provenance: "suggested",
                Removal: new(suggestion.Value!.ToString(), path + suggestion.Key,
                    ProactiveProposal.RemovalFields(suggestions, suggestion.Key).Select(field => path + field).ToArray())));
        }
    }
}
