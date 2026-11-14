using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools.Implementations;

/// <summary>A revision in a chat turn that removes every item of a preview, held until the turn ends with its answer.</summary>
public sealed record PendingPreviewDiscard(Guid PendingOperationId, string PreviewFingerprint)
{
    public const string Instruction = "The preview is discarded when your answer reaches the person. Say so in one short sentence.";
}

public sealed class RevisePendingChangesTool(IServiceProvider services) : IAiTool
{
    public const string CompletionArgumentDescription = "True for a complete pending-preview revision, on the first call; false when a question, read or fresh write still needs another tool call. A successful complete revision ends the turn with the revised preview; never repeat it to signal completion.";

    public string Name => "revise_pending_changes";
    public bool IsReadOnly => true;
    public string Description => "Correct an existing pending preview with only the requested field edits. Keep every unchanged field and item, including when adding an optional field. Only explicitly removed item IDs are removed. Set answer_question to true when the person also asks an opinion, clarification or other question, and answer in one short sentence without figures alongside this call. For a question without edits, pass empty items and omit remove_item_ids. For edits without a question, set answer_question to false and emit no prose. Set orbit_read_complete=true on the first revision call when it covers the entire correction and that argument is exposed. Set it to false only when another tool call is needed, such as a lookup or fresh write in the same request. After a successful revision, do not repeat the same edit to mark completion. This never applies account changes.";
    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            pending_operation_id = new { type = JsonSchemaTypes.String },
            answer_question = new { type = JsonSchemaTypes.Boolean, description = "True when this turn needs an answer beside the preview, including when also revising fields; false for a revision alone." },
            items = new
            {
                type = JsonSchemaTypes.Array,
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new { remove_suggested_fields = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String }, description = "Suggested field names or slash-separated paths to remove while preserving stored and requested values. Use only fields with removal metadata." }, item_id = new { type = JsonSchemaTypes.String, description = "Exact item_id from the pending preview for the entity being edited. It may differ from entity_id." }, edits_json = new { type = JsonSchemaTypes.String, description = "JSON object containing only the requested changes, using the operation schema field names and types. Optional fields may be absent from the preview." } },
                    required = new[] { "item_id", "edits_json" }
                }
            },
            remove_item_ids = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String }, description = "Exact item_ids to discard from this preview. Never list an item that should remain. Skipping a habit occurrence keeps its skip preview item; it does not remove it. Omit when no preview items should be discarded." }
        },
        required = new[] { "pending_operation_id", "items" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteAsync(args, userId, ct, null);

    /// <summary>
    /// Revises a pending preview. A revision that removes every item discards the preview. With
    /// <paramref name="holdDiscard"/>, a chat turn gets the discard to run when it ends with its answer, so a withdrawn
    /// turn discards nothing.
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct, AgentClientContext? clientContext,
        bool holdDiscard = false)
    {
        if (!Guid.TryParse(JsonArgumentParser.GetOptionalString(args, "pending_operation_id"), out var id))
            return new(false, Error: "pending_operation_not_found");
        var store = services.GetRequiredService<IPendingAgentOperationStore>();
        var stored = store.GetExecution(userId, id);
        if (stored is null || !store.IsAwaitingConfirmation(userId, id)) return new(false, Error: "pending_operation_not_found");
        if (stored.RequiresReproposal) return new(false, Error: "pending_operation_schema_changed");
        var previewer = services.GetRequiredService<IPendingOperationChangePreviewer>();
        var original = await previewer.PreviewAsync(userId, stored.OperationId, stored.Arguments, ct, clientContext);
        if (original.IsFailure || original.Value?.Items is not { } offered) return new(false, Error: "preview_unavailable");
        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var removedFields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        try
        {
            foreach (var item in args.GetProperty("items").EnumerateArray())
            {
                var itemId = item.GetProperty("item_id").GetString()!;
                removedFields[itemId] = JsonArgumentParser.ParseStringArray(item, "remove_suggested_fields") ?? [];
                using var json = JsonDocument.Parse(item.GetProperty("edits_json").GetString()!);
                if (!edits.TryAdd(itemId, json.RootElement.Clone())) return new(false, Error: "invalid_revision");
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentNullException)
        {
            return new(false, Error: "invalid_revision");
        }
        var removed = (JsonArgumentParser.ParseStringArray(args, "remove_item_ids") ?? []).ToHashSet(StringComparer.Ordinal);
        if (edits.Keys.Concat(removed).Any(itemId => offered.All(item => item.ItemId != itemId))) return new(false, Error: "item_not_offered");
        if (IsInspection(args)) return PendingPreview(original.Value);
        var selected = offered.Where(item => !removed.Contains(item.ItemId)).Select(item =>
            new RevisedPendingOperationItem(item.ItemId, edits.TryGetValue(item.ItemId, out var change) ? change : null,
                RemoveSuggestedFields: removedFields.GetValueOrDefault(item.ItemId))).ToArray();
        if (holdDiscard && selected.Length == 0)
            return new(true, Payload: new PendingPreviewDiscard(id, original.Value.PreviewFingerprint!));
        var revision = await services.GetRequiredService<PendingOperationRevisionService>().ReviseAsync(userId, id,
            new(original.Value.PreviewFingerprint!, selected, clientContext), ct);
        if (!revision.IsSuccess || revision.Preview is not { } preview) return new(false, Error: revision.Error ?? "invalid_revision");
        return PendingPreview(preview);

        ToolResult PendingPreview(PendingOperationChangePreview value)
        {
            var capability = services.GetRequiredService<IAgentCatalogService>().GetCapability(stored.CapabilityId)
                ?? throw new InvalidOperationException("The pending operation capability is missing.");
            return new(true, Payload: new PendingAgentOperation(id, stored.CapabilityId, stored.OperationId, stored.OperationId,
                capability.RiskClass, stored.ConfirmationRequirement,
                stored.ExpiresAtUtc ?? throw new InvalidOperationException("The pending operation expiry is missing."),
                value.Changes, value.ChangeTargetCount, value.Items,
                value.PreviewFingerprint, stored.ActionKey));
        }
    }

    /// <summary>Runs a discard that a chat turn held. The preview must still be the one that the revision emptied.</summary>
    public async Task<bool> DiscardAsync(Guid userId, PendingPreviewDiscard discard, AgentClientContext? clientContext, CancellationToken ct) =>
        (await services.GetRequiredService<PendingOperationRevisionService>().ReviseAsync(userId, discard.PendingOperationId,
            new(discard.PreviewFingerprint, [], clientContext), ct)).Cancelled;

    internal static bool IsInspection(JsonElement args) =>
        args.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 0
        && (!args.TryGetProperty("remove_item_ids", out var removed)
            || removed.ValueKind == JsonValueKind.Array && removed.GetArrayLength() == 0);
}
