using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class RevisePendingChangesTool(IServiceProvider services) : IAiTool
{
    public string Name => "revise_pending_changes";
    public bool IsReadOnly => true;
    public string Description => "Correct an existing pending preview. Keep every unchanged field and item. Only explicitly removed item IDs are removed. This revises a proposal and never applies account changes.";
    public object GetParameterSchema() => new
    {
        type = "object",
        properties = new
        {
            pending_operation_id = new { type = "string" },
            items = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new { item_id = new { type = "string" }, edits_json = new { type = "string", description = "JSON object containing only the changed fields, in the existing preview field names and types." } },
                    required = new[] { "item_id", "edits_json" }
                }
            },
            remove_item_ids = new { type = "array", items = new { type = "string" } }
        },
        required = new[] { "pending_operation_id", "items" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!Guid.TryParse(JsonArgumentParser.GetOptionalString(args, "pending_operation_id"), out var id))
            return new(false, Error: "pending_operation_not_found");
        var store = services.GetRequiredService<IPendingAgentOperationStore>();
        var stored = store.GetExecution(userId, id);
        if (stored is null || !store.IsAwaitingConfirmation(userId, id)) return new(false, Error: "pending_operation_not_found");
        var previewer = services.GetRequiredService<IPendingOperationChangePreviewer>();
        var original = await previewer.PreviewAsync(userId, stored.OperationId, stored.Arguments, ct);
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
        var selected = offered.Where(item => !removed.Contains(item.ItemId)).Select(item =>
            new RevisedPendingOperationItem(item.ItemId, edits.TryGetValue(item.ItemId, out var change) ? change : null)).ToArray();
        var revision = await services.GetRequiredService<PendingOperationRevisionService>().ReviseAsync(userId, id,
            new(original.Value.PreviewFingerprint!, selected), ct);
        if (!revision.IsSuccess || revision.Preview is not { } preview) return new(false, Error: revision.Error ?? "invalid_revision");
        var capability = services.GetRequiredService<IAgentCatalogService>().GetCapability(stored.CapabilityId)
            ?? throw new InvalidOperationException("The pending operation capability is missing.");
        return new(true, Payload: new PendingAgentOperation(id, stored.CapabilityId, stored.OperationId, stored.OperationId,
            capability.RiskClass, stored.ConfirmationRequirement,
            stored.ExpiresAtUtc ?? throw new InvalidOperationException("The pending operation expiry is missing."),
            preview.Changes, preview.ChangeTargetCount, preview.Items,
            preview.PreviewFingerprint, stored.ActionKey));
    }
}
