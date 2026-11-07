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
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly string[] BulkSummaryFields = ["partial", "applied_count", "total_matched", "skipped_count"];
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
    /// Keeps the approved preview in full when it fits the stored budget. Then removes the edit choices and the
    /// validation notes from each item, and last the flat change list, so the item names and the field values stay.
    /// Returns null when the preview does not fit.
    /// </summary>
    public static string? SerializePreview(PendingOperationChangePreview preview)
    {
        var json = JsonSerializer.Serialize(preview, WireOptions);
        if (json.Length <= PendingAgentOperationState.MaxRevisedPreviewJsonLength) return json;
        var items = preview.Items?.Select(item => item with { FieldOptions = null, ValidationErrors = null }).ToArray();
        json = JsonSerializer.Serialize(preview with { Items = items }, WireOptions);
        if (json.Length <= PendingAgentOperationState.MaxRevisedPreviewJsonLength) return json;
        json = JsonSerializer.Serialize(preview with { Changes = [], Items = items }, WireOptions);
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
