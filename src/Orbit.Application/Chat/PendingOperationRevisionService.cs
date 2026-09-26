using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed record RevisedPendingOperationItem(string ItemId, JsonElement? Edits = null);

public sealed record RevisePendingOperationRequest(
    string PreviewFingerprint,
    IReadOnlyList<RevisedPendingOperationItem> Items);

public sealed record PendingOperationRevisionResult(
    bool IsSuccess,
    string? Error,
    PendingAgentOperationExecution? Operation,
    PendingOperationChangePreview? Preview,
    bool Cancelled = false);

public sealed class PendingOperationRevisionService(
    IPendingAgentOperationStore store,
    IPendingOperationChangePreviewer previewer,
    IValidator<RevisePendingOperationRequest> validator)
{
    public async Task<PendingOperationRevisionResult> ReviseAsync(
        Guid userId, Guid pendingOperationId, RevisePendingOperationRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return Failure("invalid_revision");

        var execution = store.GetExecution(userId, pendingOperationId);
        if (execution is null)
            return Failure("pending_operation_not_found");

        var original = await previewer.PreviewAsync(userId, execution.OperationId,
            execution.Arguments, cancellationToken);
        if (original?.Items is null || original.PreviewFingerprint != request.PreviewFingerprint)
            return Failure("stale_preview");

        var offered = original.Items.ToDictionary(item => item.ItemId, StringComparer.Ordinal);
        if (request.Items.Any(item => !offered.ContainsKey(item.ItemId)))
            return Failure("item_not_offered");

        foreach (var item in request.Items)
        {
            if (item.Edits is not { } edits || edits.ValueKind == JsonValueKind.Null)
                continue;
            if (edits.ValueKind != JsonValueKind.Object)
                return Failure("invalid_edits");
            var allowed = offered[item.ItemId].Fields.Select(field => field.Field)
                .ToHashSet(StringComparer.Ordinal);
            if (edits.EnumerateObject().Any(field => !allowed.Contains(field.Name)))
                return Failure("field_not_offered");
        }

        var expectedFingerprint = AgentOperationFingerprint.Compute(
            execution.OperationId, execution.Arguments.GetRawText());
        if (request.Items.Count == 0)
        {
            return store.Cancel(userId, pendingOperationId, expectedFingerprint)
                ? new PendingOperationRevisionResult(true, null, null, null, Cancelled: true)
                : Failure("revision_conflict");
        }

        var revised = BuildArguments(execution.OperationId, execution.Arguments, request.Items, offered);
        if (revised is null)
            return Failure("invalid_revision");
        if (execution.OperationId == "bulk_create_habits"
            && !ValidateCreate(userId, revised.Value))
            return Failure("invalid_revision");

        var revisedPreview = await previewer.PreviewAsync(userId, execution.OperationId,
            revised.Value, cancellationToken);
        if (revisedPreview?.Items is null || revisedPreview.Items.Count != request.Items.Count
            || revisedPreview.Items.Any(item => !offered.TryGetValue(item.ItemId, out var previous)
                || execution.OperationId != "bulk_create_habits"
                    && item.StateFingerprint != previous.StateFingerprint))
            return Failure("stale_preview");

        var revisedJson = revised.Value.GetRawText();
        var revisedFingerprint = AgentOperationFingerprint.Compute(execution.OperationId, revisedJson);
        if (!store.Revise(userId, pendingOperationId, expectedFingerprint, revisedJson,
            revisedFingerprint, revisedPreview.PreviewFingerprint!))
            return Failure("revision_conflict");

        return new PendingOperationRevisionResult(true, null,
            store.GetExecution(userId, pendingOperationId), revisedPreview);
    }

    public async Task<bool> IsCurrentAsync(Guid userId, PendingAgentOperationExecution execution,
        CancellationToken cancellationToken)
    {
        if (execution.PreviewFingerprint is null)
            return true;
        var current = await previewer.PreviewAsync(userId, execution.OperationId,
            execution.Arguments, cancellationToken);
        return current?.PreviewFingerprint == execution.PreviewFingerprint;
    }

    private static JsonElement? BuildArguments(string operationId, JsonElement original,
        IReadOnlyList<RevisedPendingOperationItem> selected,
        IReadOnlyDictionary<string, PendingOperationItem> offered)
    {
        if (JsonNode.Parse(original.GetRawText()) is not JsonObject root)
            return null;

        var built = operationId.StartsWith("bulk_", StringComparison.Ordinal)
            || operationId == "delete_habit"
            ? BuildHabitArguments(root, operationId, selected, offered)
            : BuildOtherArguments(root, operationId, selected);
        if (!built)
            return null;

        return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }

    private static bool BuildHabitArguments(JsonObject root, string operationId,
        IReadOnlyList<RevisedPendingOperationItem> selected,
        IReadOnlyDictionary<string, PendingOperationItem> offered)
    {
        if (operationId == "bulk_create_habits")
            return BuildCreateArguments(root, selected);
        if (operationId is "bulk_update_habits" or "bulk_reschedule_habits")
            return BuildUpdatedHabitArguments(root, operationId, selected);
        if (operationId is "bulk_log_habits" or "bulk_skip_habits")
            return BuildDatedHabitArguments(root, selected, offered);
        if (operationId == "bulk_update_habit_emojis")
            return BuildEmojiArguments(root, selected);
        if (operationId == "bulk_delete_habits")
        {
            if (selected.Any(HasEdits))
                return false;
            root.Remove("filter");
            root["habit_ids"] = new JsonArray(selected.Select(item =>
                (JsonNode?)JsonValue.Create(item.ItemId)).ToArray());
            return true;
        }
        return operationId == "delete_habit" && selected.Count == 1 && !HasEdits(selected[0]);
    }

    private static bool BuildOtherArguments(JsonObject root, string operationId,
        IReadOnlyList<RevisedPendingOperationItem> selected)
    {
        if (operationId is "delete_goal" or "delete_tag" or "delete_checklist_template")
            return selected.Count == 1 && !HasEdits(selected[0]);
        if (operationId == "manage_calendar_sync")
            return BuildCalendarArguments(root, selected);
        if (operationId == "delete_user_facts")
            return BuildSelectedDeletion(root, selected, "fact_id", "fact_ids");
        if (operationId == "delete_notifications")
        {
            if (!BuildSelectedDeletion(root, selected, "notification_id", "notification_ids"))
                return false;
            root["action"] = "delete_selected";
            return true;
        }
        return false;
    }

    private static bool BuildCalendarArguments(JsonObject root,
        IReadOnlyList<RevisedPendingOperationItem> selected)
    {
        if (selected.Count != 1)
            return false;
        if (root["action"]?.ToString() != "set_auto_sync")
            return !HasEdits(selected[0]);
        if (selected[0].Edits is { ValueKind: JsonValueKind.Object } edits
            && edits.TryGetProperty("enabled", out var enabled)
            && enabled.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            return false;
        return ApplyEdits(root, selected[0].Edits);
    }

    private static bool HasEdits(RevisedPendingOperationItem item) =>
        item.Edits is { ValueKind: JsonValueKind.Object } edits
        && edits.EnumerateObject().Any();

    private static bool BuildSelectedDeletion(JsonObject root,
        IReadOnlyList<RevisedPendingOperationItem> selected, string singleKey, string multipleKey)
    {
        if (selected.Any(HasEdits))
            return false;
        root.Remove(singleKey);
        root[multipleKey] = new JsonArray(selected.Select(item =>
            (JsonNode?)JsonValue.Create(item.ItemId)).ToArray());
        return true;
    }

    private static bool BuildCreateArguments(JsonObject root,
        IReadOnlyList<RevisedPendingOperationItem> selected)
    {
        if (root["habits"] is not JsonArray habits)
            return false;
        var revisedHabits = new JsonArray();
        foreach (var item in selected)
        {
            var habit = habits.OfType<JsonObject>().FirstOrDefault(candidate =>
                candidate["preview_item_id"]?.ToString() == item.ItemId);
            if (habit is null && int.TryParse(item.ItemId, out var index)
                && index >= 0 && index < habits.Count)
                habit = habits[index] as JsonObject;
            if (habit is null)
                return false;
            var copy = (JsonObject)habit.DeepClone();
            if (!ApplyEdits(copy, item.Edits))
                return false;
            copy["preview_item_id"] = item.ItemId;
            revisedHabits.Add(copy);
        }
        root["habits"] = revisedHabits;
        return true;
    }

    private static bool BuildUpdatedHabitArguments(JsonObject root, string operationId,
        IReadOnlyList<RevisedPendingOperationItem> selected)
    {
        var updates = operationId == "bulk_reschedule_habits"
            ? new JsonObject { ["due_date"] = root["due_date"]?.DeepClone() }
            : root["updates"]?.DeepClone() as JsonObject;
        if (updates is null)
            return false;
        var items = new JsonArray();
        foreach (var item in selected)
        {
            var previous = root["revised_items"] as JsonArray;
            var previousUpdates = previous?.OfType<JsonObject>().FirstOrDefault(candidate =>
                candidate["habit_id"]?.ToString() == item.ItemId)?["updates"] as JsonObject;
            var copy = (JsonObject)(previousUpdates ?? updates).DeepClone();
            if (!ApplyEdits(copy, item.Edits))
                return false;
            items.Add(new JsonObject
            {
                ["habit_id"] = item.ItemId,
                ["updates"] = copy
            });
        }
        root["revised_items"] = items;
        root.Remove("filter");
        return true;
    }

    private static bool BuildDatedHabitArguments(JsonObject root,
        IReadOnlyList<RevisedPendingOperationItem> selected,
        IReadOnlyDictionary<string, PendingOperationItem> offered)
    {
        var items = new JsonArray();
        foreach (var item in selected)
        {
            var originalDate = offered[item.ItemId].Fields
                .FirstOrDefault(field => field.Field == "date")?.NewValue;
            if (originalDate is null)
                return false;
            var edits = item.Edits;
            string? date = originalDate;
            if (edits is { ValueKind: JsonValueKind.Object } values
                && values.TryGetProperty("date", out var editedDate))
            {
                if (editedDate.ValueKind != JsonValueKind.String)
                    return false;
                date = editedDate.GetString();
            }
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
                return false;
            items.Add(new JsonObject { ["habit_id"] = item.ItemId, ["date"] = date });
        }
        root.Remove("filter");
        root.Remove("habit_ids");
        root["revised_items"] = items;
        return true;
    }

    private static bool BuildEmojiArguments(JsonObject root,
        IReadOnlyList<RevisedPendingOperationItem> selected)
    {
        var items = new JsonArray();
        foreach (var item in selected)
        {
            var target = new JsonObject { ["habit_id"] = item.ItemId };
            var previous = root["revised_items"] as JsonArray;
            var previousEmoji = previous?.OfType<JsonObject>().FirstOrDefault(candidate =>
                candidate["habit_id"]?.ToString() == item.ItemId);
            if (previousEmoji?.ContainsKey("emoji") == true)
                target["emoji"] = previousEmoji["emoji"]?.DeepClone();
            if (item.Edits is { ValueKind: JsonValueKind.Object } edits
                && edits.TryGetProperty("emoji", out var emoji))
            {
                if (emoji.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
                    return false;
                target["emoji"] = JsonNode.Parse(emoji.GetRawText());
            }
            items.Add(target);
        }
        root.Remove("filter");
        root.Remove("habit_ids");
        root["revised_items"] = items;
        return true;
    }

    private static bool ApplyEdits(JsonObject target, JsonElement? edits)
    {
        if (edits is not { ValueKind: JsonValueKind.Object } fields)
            return true;
        foreach (var field in fields.EnumerateObject())
            target[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        return true;
    }

    private static bool ValidateCreate(Guid userId, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("habits", out var habits)
            || habits.ValueKind != JsonValueKind.Array)
            return false;
        var items = new List<BulkHabitItem>();
        foreach (var habit in habits.EnumerateArray())
        {
            if (habit.ValueKind != JsonValueKind.Object)
                return false;
            var item = BulkCreateHabitsTool.ParseBulkHabitItem(habit);
            if (item is null)
                return false;
            items.Add(item);
        }
        return new BulkCreateHabitsCommandValidator()
            .Validate(new BulkCreateHabitsCommand(userId, items)).IsValid;
    }

    private static PendingOperationRevisionResult Failure(string error) =>
        new(false, error, null, null);
}
