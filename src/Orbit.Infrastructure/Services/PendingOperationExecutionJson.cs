using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Writes an executed operation and an approved preview in the same JSON shape that the API responses send to the client.
/// The MVC options use the web defaults and a <see cref="JsonStringEnumConverter"/>, so this writer uses the same options.
/// </summary>
internal static class PendingOperationExecutionJson
{
    /// <summary>
    /// The relaxed encoder writes each non-ASCII character as one character and not as a six-character escape, so the
    /// length checks measure the text. The jsonb column stores the decoded text in both cases.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// The budget of the richer preview tiers. Only the smallest tier can use the full stored budget.
    /// </summary>
    internal const int RichPreviewJsonLength = 16_000;
    private const string TitleField = "title";

    private static readonly string[] BulkSummaryFields = ["partial", "applied_count", "total_matched", "skipped_count", "unchanged_count"];
    private static readonly string[] BulkItemFields = ["index", "status", "habitId"];
    private static readonly string[] ClearedBulkItemFields = ["title", "error", "field"];

    /// <summary>
    /// Keeps the full result when it fits the stored budget. Then keeps a compact bulk result (each item's index,
    /// status and habit id, plus the totals) in place of the payload, removes the read block, and last the free text.
    /// Each compact item writes its title, error and field as null, so it still has every key of the client item schema.
    /// The status and the target ids always stay.
    /// </summary>
    public static string Serialize(AgentOperationResult operation)
    {
        var json = JsonSerializer.Serialize(operation, WireOptions);
        if (json.Length <= PendingAgentOperationState.MaxExecutionJsonLength) return json;
        var compact = CompactBulkPayload(operation.Payload);
        json = JsonSerializer.Serialize(operation with { Payload = compact, ReadBlock = null }, WireOptions);
        if (json.Length <= PendingAgentOperationState.MaxExecutionJsonLength) return json;
        json = JsonSerializer.Serialize(operation with
        {
            Payload = compact,
            ReadBlock = null,
            Summary = null,
            TargetName = null,
            PolicyReason = null
        }, WireOptions);
        if (json.Length <= PendingAgentOperationState.MaxExecutionJsonLength) return json;
        return JsonSerializer.Serialize(operation with
        {
            Payload = null,
            ReadBlock = null,
            Summary = null,
            TargetName = null,
            PolicyReason = null
        }, WireOptions);
    }

    /// <summary>
    /// Keeps the approved preview in full when it fits the rich budget. Then removes the edit choices and the
    /// validation notes from each item, and then the flat change list. The smallest tier keeps, for each item, its id,
    /// its entity id, its name, its emoji and its title field, so a batch of 100 items fits the stored budget.
    /// Returns null when the preview does not fit.
    /// </summary>
    public static string? SerializePreview(PendingOperationChangePreview preview)
    {
        var json = JsonSerializer.Serialize(preview, WireOptions);
        if (json.Length <= RichPreviewJsonLength) return json;
        var items = preview.Items?.Select(item => item with { FieldOptions = null, ValidationErrors = null }).ToArray();
        json = JsonSerializer.Serialize(preview with { Items = items }, WireOptions);
        if (json.Length <= RichPreviewJsonLength) return json;
        json = JsonSerializer.Serialize(preview with { Changes = [], Items = items }, WireOptions);
        if (json.Length <= RichPreviewJsonLength) return json;
        var titles = preview.Items?.Select(item => item with
        {
            Fields = item.Fields.Where(field => field.Field == TitleField)
                .Select(field => field with { EntityEmoji = null, Provenance = null, Removal = null }).ToArray(),
            ValidationErrors = null,
            FieldOptions = null,
            Placement = null,
            HabitType = null
        }).ToArray();
        json = JsonSerializer.Serialize(preview with { Changes = [], Items = titles }, WireOptions);
        return json.Length <= PendingAgentOperationState.MaxRevisedPreviewJsonLength ? json : null;
    }

    private static JsonObject? CompactBulkPayload(object? payload)
    {
        if (JsonSerializer.SerializeToNode(payload, WireOptions) is not JsonObject source
            || source["results"] is not JsonArray results)
            return null;
        var compact = new JsonObject();
        foreach (var field in BulkSummaryFields)
            if (source.TryGetPropertyValue(field, out var value))
                compact[field] = value?.DeepClone();
        var items = new JsonArray();
        foreach (var result in results)
        {
            if (result is not JsonObject item) continue;
            var kept = new JsonObject();
            foreach (var field in BulkItemFields)
                kept[field] = item.TryGetPropertyValue(field, out var value) ? value?.DeepClone() : null;
            foreach (var field in ClearedBulkItemFields)
                kept[field] = null;
            items.Add(kept);
        }
        compact["results"] = items;
        return compact;
    }
}
