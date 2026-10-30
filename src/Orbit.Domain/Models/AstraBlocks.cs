using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Domain.Models;

public sealed record AstraBlockOption(string Id, string Label, string? Description = null);
public sealed record AstraBlockItem(string Id, string Label, JsonElement Arguments, string? Detail = null);
public sealed record AstraDraftClarification(string DraftCallId, string MissingField, Guid ClarificationId);
public sealed record AstraBlockDefinition(
    string Form,
    string Title,
    IReadOnlyList<AstraBlockOption> Options,
    IReadOnlyList<AstraBlockItem> Items,
    bool MultiSelect = false,
    bool AllowFreeText = false,
    string? Action = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AstraQuestion>? Questions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Intro = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AstraQuestionProgress? Progress = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AstraDraftClarification? DraftClarification = null);

public sealed record AstraBlockAnswer(Guid BlockId, IReadOnlyList<string> SelectedIds, string? FreeText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AstraQuestionAnswer>? Answers = null);
public sealed record AstraPickListItem(string Id, string Label, string? Detail);
public sealed record AstraChoiceBlock(Guid BlockId, string Question, IReadOnlyList<AstraBlockOption> Options,
    bool MultiSelect, bool AllowFreeText, DateTime ExpiresAtUtc);
public sealed record AstraPickListBlock(Guid BlockId, string Title, IReadOnlyList<AstraPickListItem> Items,
    string Action, DateTime ExpiresAtUtc);
