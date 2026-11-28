using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed partial class ConversationTurnRecorder
{
    private async Task<Dictionary<Guid, string?>> ReadResponsesWithClosedBlocksAsync(Guid userId,
        IReadOnlyList<ConversationTurn> page, CancellationToken cancellationToken)
    {
        var responses = page.Where(row => row.ResponseJson is not null)
            .Select(row => (row.Id, Response: JsonNode.Parse(row.ResponseJson!)!))
            .Select(row => (row.Id, row.Response, Blocks: IssuedBlocks(row.Response).ToArray()))
            .Where(row => row.Blocks.Length > 0).ToArray();
        if (responses.Length == 0) return [];
        var ids = responses.SelectMany(row => row.Blocks).Select(block => block.Id).Distinct().ToArray();
        var closed = await db.IssuedAiBlocks.AsNoTracking()
            .Where(block => block.UserId == userId && ids.Contains(block.Id)
                && block.AnsweredAtUtc == null && block.InvalidatedAtUtc != null)
            .ToDictionaryAsync(block => block.Id, block => block.InvalidatedAtUtc!.Value, cancellationToken);
        var changed = new Dictionary<Guid, string?>();
        foreach (var row in responses)
        {
            foreach (var (id, block) in row.Blocks)
            {
                if (!closed.TryGetValue(id, out var closedAtUtc)) continue;
                block["closedAtUtc"] = DateTime.SpecifyKind(closedAtUtc, DateTimeKind.Utc);
                changed[row.Id] = row.Response.ToJsonString();
            }
        }
        return changed;
    }

    private static IEnumerable<(Guid Id, JsonObject Block)> IssuedBlocks(JsonNode response)
    {
        if (response["parts"] is not JsonArray parts) yield break;
        foreach (var part in parts.OfType<JsonObject>())
        {
            if (part["kind"]?.GetValue<string>() is AiBlockKinds.Choice or AiBlockKinds.PickList or AiBlockKinds.Question
                && part["block"] is JsonObject block && block["blockId"] is JsonValue value && value.TryGetValue(out Guid id))
                yield return (id, block);
        }
    }
}
