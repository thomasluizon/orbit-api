using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Domain.Models;

public sealed record AiBlockOption(string Id, string Label, string? Description = null);
public sealed record AiBlockItem(string Id, string Label, JsonElement Arguments, string? Detail = null);
public sealed record AiDraftClarification(string DraftCallId, string MissingField, Guid ClarificationId);
public sealed record AiBlockDefinition(
    string Form,
    string Title,
    IReadOnlyList<AiBlockOption> Options,
    IReadOnlyList<AiBlockItem> Items,
    bool MultiSelect = false,
    bool AllowFreeText = false,
    string? Action = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AiQuestion>? Questions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Intro = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AiQuestionProgress? Progress = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AiDraftClarification? DraftClarification = null);

public sealed record AiBlockAnswer(Guid BlockId, IReadOnlyList<string> SelectedIds, string? FreeText = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AiQuestionAnswer>? Answers = null);
public sealed record AiPickListItem(string Id, string Label, string? Detail);
public sealed record AiChoiceBlock(Guid BlockId, string Question, IReadOnlyList<AiBlockOption> Options,
    bool MultiSelect, bool AllowFreeText, DateTime ExpiresAtUtc);
public sealed record AiPickListBlock(Guid BlockId, string Title, IReadOnlyList<AiPickListItem> Items,
    string Action, DateTime ExpiresAtUtc);
