using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat;

public sealed partial class PendingOperationChangePreviewer
{
    private async Task<PendingOperationChangePreview?> PreviewPlacementAsync(Guid userId, string operationId,
        JsonElement arguments, CancellationToken ct, AgentClientContext? clientContext)
    {
        var root = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        var sources = operationId == "bulk_create_habits"
            ? (root["habits"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [] : [root];
        NormalizePlacementSources(root, sources, clientContext?.SupportsPendingOperationPlacement == true);
        if (!sources.Any(source => HasPlacementDestination(source, root)))
        {
            var bound = JsonSerializer.SerializeToElement(root);
            var unchanged = await PreviewChangesAsync(userId, operationId, bound, ct);
            return unchanged is null ? null : unchanged with { ExecutionArguments = bound };
        }
        var habits = await habitRepository.FindAsync(habit => habit.UserId == userId && !habit.IsDeleted, ct);
        var placements = new Dictionary<int, PendingOperationPlacement>();
        for (var index = 0; index < sources.Length; index++)
        {
            var source = sources[index];
            if (source["habit_id"] is not null) continue;
            var parent = source["parent_habit_id"] ?? root["parent_habit_id"];
            if (!Guid.TryParse(parent?.ToString(), out var destinationId)) continue;
            var destination = habits.FirstOrDefault(habit => habit.Id == destinationId);
            if (destination is null) return null;
            if (clientContext?.SupportsPendingOperationPlacement != true) continue;
            var selected = source[HabitPlacementRecommendation.Argument]?.ToString();
            if (selected is not null && HabitPlacement.Validate(selected, destinationId).IsFailure) return null;
            var placement = HabitPlacementRecommendation.For(destination, habits, selected);
            source[HabitPlacementRecommendation.Argument] = placement.Selected;
            placements[index] = placement;
        }
        var normalized = JsonSerializer.SerializeToElement(root);
        var preview = await PreviewChangesAsync(userId, operationId, normalized, ct);
        if (preview is null) return null;
        if (preview.Items is not { } items || placements.Count == 0)
            return preview with { ExecutionArguments = normalized };
        var updated = items.Select((item, index) => placements.TryGetValue(index, out var placement)
            ? item with
            {
                Placement = placement,
                Fields = item.Fields.Where(field => field.Field != HabitPlacementRecommendation.Argument).ToArray()
            } : item).ToArray();
        return BuildPreview(operationId, preview.Changes.Where(field => field.Field != HabitPlacementRecommendation.Argument).ToArray(), updated)!
            with
        { ExecutionArguments = normalized };
    }

    private static bool HasPlacementDestination(JsonObject source, JsonObject root) =>
        source["habit_id"] is null
        && Guid.TryParse((source["parent_habit_id"] ?? root["parent_habit_id"])?.ToString(), out _);

    private static void NormalizePlacementSources(JsonObject root, JsonObject[] sources, bool supportsPlacement)
    {
        foreach (var source in sources)
        {
            RemoveNestedPlacements(source);
            if (!supportsPlacement || !HasPlacementDestination(source, root))
                source.Remove(HabitPlacementRecommendation.Argument);
        }
    }

    private static void RemoveNestedPlacements(JsonObject source)
    {
        if (source["sub_habits"] is not JsonArray children) return;
        foreach (var child in children.OfType<JsonObject>())
        {
            child.Remove(HabitPlacementRecommendation.Argument);
            RemoveNestedPlacements(child);
        }
    }
}
