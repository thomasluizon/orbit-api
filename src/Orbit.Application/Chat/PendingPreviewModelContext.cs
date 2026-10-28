using System.Text.Json;

namespace Orbit.Application.Chat;

internal static class PendingPreviewModelContext
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public const string Instruction = "Match the requested entity to entity_id and entity_name, then use its exact item_id for revision. Item IDs are opaque and can differ from entity IDs; never infer them from array position. remove_item_ids contains the items to discard, never the items to keep. For only X or só X, retain X and remove only the other preview items when revising that preview. A request to skip a habit occurrence does not mean remove that habit's preview item. A fresh skip request uses skip_habit or bulk_skip_habits for the named habit ID, even when an unrelated preview exists. Before calling revise_pending_changes, check that the remaining items describe the requested entities and actions.";

    public static string Serialize(object previews) => "Pending preview data: " + JsonSerializer.Serialize(previews, Options);
}
