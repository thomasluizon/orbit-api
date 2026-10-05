using System.Globalization;
using System.Text.Json;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

/// <summary>
/// The target a held write points at, when the arguments name one. The current values let
/// the preview show what the write replaces.
/// </summary>
public sealed record AgentPreviewTarget(
    Guid EntityId,
    string EntityName,
    IReadOnlyDictionary<string, string?> CurrentValues,
    string StateFingerprint);

/// <summary>
/// Builds the approval preview of a held chat write from its own arguments: one item, one row
/// per argument, and the current value of every argument the target already holds. A row is
/// editable only when the editable schema declares its field. The previewer passes the schema of
/// a tool that checks its own arguments, so the revise route can hold every edit to its rules.
/// </summary>
public static class AgentArgumentPreview
{
    private static readonly HashSet<string> NameFields = new(StringComparer.Ordinal)
    {
        "title", "name", "text", "summary", "message", "subject"
    };

    private static readonly HashSet<string> FixedFields = new(StringComparer.Ordinal)
    {
        "action", "preview_item_id", "revised_items", "goal_name"
    };

    public static PendingOperationChangePreview? Build(
        string operationId,
        JsonElement arguments,
        AgentPreviewTarget? target,
        JsonElement editableSchema)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return null;

        var itemId = target is null ? "0" : target.EntityId.ToString();
        var entityId = target?.EntityId ?? Guid.Empty;
        var entityName = target?.EntityName ?? ReadName(arguments) ?? string.Empty;
        var fields = arguments.EnumerateObject()
            .Select(property => new PendingOperationChange(
                entityId,
                entityName,
                property.Name,
                target?.CurrentValues.GetValueOrDefault(property.Name),
                FormatValue(property.Value),
                ResolveValueType(property.Name, property.Value),
                property.Value.Clone(),
                IsEditable(property.Name) && AgentArgumentSchema.Declares(editableSchema, property.Name)
                    && AgentEditableArguments.IsConsumed(operationId, arguments, property.Name)
                    && !(operationId == "skip_habit" && property.Name == "date"
                        && target?.CurrentValues.GetValueOrDefault("frequency_unit") == string.Empty)))
            .ToList();

        var item = new PendingOperationItem(itemId, target?.EntityId, entityName, fields,
            target?.StateFingerprint ?? AgentOperationFingerprint.Compute(operationId, itemId),
            RemovesData: operationId is "create_habit" or "log_habit" or "skip_habit" or "update_habit"
                ? false : null);
        var fingerprint = AgentOperationFingerprint.Compute(
            operationId, JsonSerializer.Serialize<IReadOnlyList<PendingOperationItem>>([item]));

        return new PendingOperationChangePreview(fields, 1, [item], fingerprint);
    }

    private static bool IsEditable(string field) =>
        !FixedFields.Contains(field) && !IsIdentifier(field);

    public static string? FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        _ => value.ToString()
    };

    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static bool IsIdentifier(string field) =>
        field is "id" || field.EndsWith("_id", StringComparison.Ordinal)
        || field.EndsWith("_ids", StringComparison.Ordinal);

    private static string? ReadName(JsonElement arguments)
    {
        foreach (var field in NameFields)
        {
            if (arguments.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static string ResolveValueType(string field, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return "boolean";
        if (value.ValueKind == JsonValueKind.Number)
            return "number";
        if (field == "emoji")
            return "emoji";
        if (AgentArgumentSchema.IsDateField(field))
            return "date";
        if (AgentArgumentSchema.IsTimeField(field))
            return "time";
        return "text";
    }
}
