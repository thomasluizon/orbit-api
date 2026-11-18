using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Orbit.Application.Chat.References;

/// <summary>
/// The record type of each ID argument that a model-facing schema declares, and the ID arguments with the label
/// "no reference". The same names hold for the operation schemas that <c>edits_json</c> and a pickList item use.
/// </summary>
public static partial class ReferenceArguments
{
    public static IReadOnlyDictionary<string, ReferenceRecordType> Typed { get; } =
        new Dictionary<string, ReferenceRecordType>(StringComparer.Ordinal)
        {
            ["habit_id"] = ReferenceRecordType.Habit,
            ["habit_ids"] = ReferenceRecordType.Habit,
            ["parent_habit_id"] = ReferenceRecordType.Habit,
            ["parent_id"] = ReferenceRecordType.Habit,
            ["new_parent_id"] = ReferenceRecordType.Habit,
            ["target_ids"] = ReferenceRecordType.Habit,
            ["goal_id"] = ReferenceRecordType.Goal,
            ["goal_ids"] = ReferenceRecordType.Goal,
            ["tag_id"] = ReferenceRecordType.Tag,
            ["tag_ids"] = ReferenceRecordType.Tag,
            ["template_id"] = ReferenceRecordType.ChecklistTemplate,
            ["notification_id"] = ReferenceRecordType.Notification,
            ["notification_ids"] = ReferenceRecordType.Notification,
            ["key_id"] = ReferenceRecordType.ApiKey,
            ["skip_id"] = ReferenceRecordType.Skip,
            ["pending_operation_id"] = ReferenceRecordType.PendingOperation,
            ["item_id"] = ReferenceRecordType.PreviewItem,
            ["remove_item_ids"] = ReferenceRecordType.PreviewItem,
            ["preview_item_id"] = ReferenceRecordType.PreviewItem
        };

    /// <summary>
    /// ID arguments that name no listed record and pass unchanged: a calendar suggestion GUID, Google calendar ids, the
    /// call id of a habit draft, and the question ids of an ask card. The option and item ids of an ask card are the
    /// exception that <see cref="AskRecordType"/> names.
    /// </summary>
    public static IReadOnlySet<string> Unreferenced { get; } =
        new HashSet<string>(["suggestion_id", "calendar_ids", "draft_call_id", "id"], StringComparer.Ordinal);

    /// <summary>
    /// The record type of an <c>id</c> of an ask card. An option or item id that holds a reference is the habit that the
    /// option or item stands for, so the ask guards and the issued card read its full GUID. A free id such as
    /// <c>yes</c> is no reference and stays.
    /// </summary>
    public static ReferenceRecordType? AskRecordType(string name, string? container) =>
        name == "id" && container is "options" or "items" ? ReferenceRecordType.Habit : null;

    /// <summary>The options that the provider adapter serializes a tool declaration with.</summary>
    private static readonly JsonSerializerOptions DeclarationOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The arguments whose string value holds the JSON arguments of an operation.</summary>
    public const string EditsJson = "edits_json";

    public const string PickListArguments = "arguments";

    public static bool IsIdArgument(string name) =>
        name is "id" or "ids" || name.EndsWith("_id", StringComparison.Ordinal) || name.EndsWith("_ids", StringComparison.Ordinal);

    /// <summary>
    /// The chat declaration of a tool schema: the description of each typed ID argument names a reference where it
    /// names an ID or a GUID. MCP reads the tool classes, so the tool classes keep their own descriptions.
    /// </summary>
    public static JsonNode NameReferences(object schema)
    {
        var node = JsonSerializer.SerializeToNode(schema, DeclarationOptions)
            ?? throw new InvalidOperationException("A tool schema could not be serialized.");
        Walk(node);
        return node;

        static void Walk(JsonNode? node)
        {
            if (node is JsonArray array)
            {
                foreach (var item in array) Walk(item);
                return;
            }
            if (node is not JsonObject fields) return;
            if (fields["properties"] is JsonObject properties)
                foreach (var (name, property) in properties)
                    if (Typed.ContainsKey(name) && property is JsonObject argument
                        && argument["description"] is JsonValue description && description.TryGetValue<string>(out var text))
                        argument["description"] = IdWord().Replace(text, match => match.Value.EndsWith('s') ? "references" : "reference");
            foreach (var (_, child) in fields) Walk(child);
        }
    }

    [GeneratedRegex(@"\b(?:GUID|ID)s?\b", RegexOptions.CultureInvariant)]
    private static partial Regex IdWord();
}
