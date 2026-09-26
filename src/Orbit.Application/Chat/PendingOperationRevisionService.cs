using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
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

        var revised = BuildArguments(execution.OperationId, execution.Arguments, request.Items);
        if (revised is null)
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
        IReadOnlyList<RevisedPendingOperationItem> selected)
    {
        if (JsonNode.Parse(original.GetRawText()) is not JsonObject root)
            return null;

        if (operationId == "bulk_create_habits")
        {
            if (!BuildCreateArguments(root, selected))
                return null;
        }
        else if (operationId is "bulk_update_habits" or "bulk_reschedule_habits")
        {
            var updates = operationId == "bulk_reschedule_habits"
                ? new JsonObject { ["due_date"] = root["due_date"]?.DeepClone() }
                : root["updates"]?.DeepClone() as JsonObject;
            if (updates is null)
                return null;
            var items = new JsonArray();
            foreach (var item in selected)
            {
                var copy = (JsonObject)updates.DeepClone();
                if (!ApplyEdits(copy, item.Edits))
                    return null;
                items.Add(new JsonObject
                {
                    ["habit_id"] = item.ItemId,
                    ["updates"] = copy
                });
            }
            root["revised_items"] = items;
            root.Remove("filter");
        }
        else if (operationId is "bulk_delete_habits" or "bulk_log_habits" or "bulk_skip_habits"
            or "bulk_update_habit_emojis")
        {
            if (selected.Any(item => item.Edits is { ValueKind: JsonValueKind.Object } edits
                && edits.EnumerateObject().Any()))
                return null;
            root.Remove("filter");
            root["habit_ids"] = new JsonArray(selected.Select(item =>
                (JsonNode?)JsonValue.Create(item.ItemId)).ToArray());
            if (operationId == "bulk_update_habit_emojis")
                root["include_completed"] = true;
        }
        else if (operationId == "delete_habit")
        {
            if (selected.Count != 1 || selected[0].Edits is { ValueKind: JsonValueKind.Object } edits
                && edits.EnumerateObject().Any())
                return null;
        }
        else
            return null;

        return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }

    private static bool BuildCreateArguments(JsonObject root,
        IReadOnlyList<RevisedPendingOperationItem> selected)
    {
        if (root["habits"] is not JsonArray habits)
            return false;
        var revisedHabits = new JsonArray();
        foreach (var item in selected)
        {
            if (!int.TryParse(item.ItemId, out var index) || index < 0 || index >= habits.Count
                || habits[index] is not JsonObject habit)
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

    private static bool ApplyEdits(JsonObject target, JsonElement? edits)
    {
        if (edits is not { ValueKind: JsonValueKind.Object } fields)
            return true;
        foreach (var field in fields.EnumerateObject())
            target[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        return true;
    }

    private static PendingOperationRevisionResult Failure(string error) =>
        new(false, error, null, null);
}
