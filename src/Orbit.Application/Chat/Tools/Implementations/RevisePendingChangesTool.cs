using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class RevisePendingChangesTool(IServiceProvider services) : IAiTool
{
    public string Name => "revise_pending_changes";
    public bool IsReadOnly => true;
    public string Description => "Correct an existing pending preview with only the requested field edits. Keep every unchanged field and item, including when adding an optional field. Only explicitly removed item IDs are removed. For an opinion or question about a preview, pass empty items and omit remove_item_ids to keep it beside your one-sentence answer without figures. This never applies account changes.";
    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            pending_operation_id = new { type = JsonSchemaTypes.String },
            items = new
            {
                type = JsonSchemaTypes.Array,
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new { item_id = new { type = JsonSchemaTypes.String }, edits_json = new { type = JsonSchemaTypes.String, description = "JSON object containing only the changed fields, in the existing preview field names and types." } },
                    required = new[] { "item_id", "edits_json" }
                }
            },
            remove_item_ids = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } }
        },
        required = new[] { "pending_operation_id", "items" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteAsync(args, userId, ct, null);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct, AgentClientContext? clientContext)
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
        try
        {
            foreach (var item in args.GetProperty("items").EnumerateArray())
            {
                var itemId = item.GetProperty("item_id").GetString()!;
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
            new RevisedPendingOperationItem(item.ItemId, edits.TryGetValue(item.ItemId, out var change) ? change : null)).ToArray();
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

    internal static bool IsInspection(JsonElement args) =>
        args.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 0
        && (!args.TryGetProperty("remove_item_ids", out var removed)
            || removed.ValueKind == JsonValueKind.Array && removed.GetArrayLength() == 0);
}
