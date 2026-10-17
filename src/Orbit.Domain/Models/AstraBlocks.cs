using System.Text.Json;

namespace Orbit.Domain.Models;

public sealed record AstraBlockOption(string Id, string Label, string? Description = null);
public sealed record AstraBlockItem(string Id, string Label, JsonElement Arguments, string? Detail = null);
public sealed record AstraBlockDefinition(
    string Form,
    string Title,
    IReadOnlyList<AstraBlockOption> Options,
    IReadOnlyList<AstraBlockItem> Items,
    bool MultiSelect = false,
    bool AllowFreeText = false,
    string? Action = null);

public sealed record AstraBlockAnswer(Guid BlockId, IReadOnlyList<string> SelectedIds, string? FreeText = null);
public sealed record AstraPickListItem(string Id, string Label, string? Detail);
public sealed record AstraChoiceBlock(Guid BlockId, string Question, IReadOnlyList<AstraBlockOption> Options,
    bool MultiSelect, bool AllowFreeText, DateTime ExpiresAtUtc);
public sealed record AstraPickListBlock(Guid BlockId, string Title, IReadOnlyList<AstraPickListItem> Items,
    string Action, DateTime ExpiresAtUtc);
