using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Models;

public sealed record AskToolArguments(
    string Form,
    string? Question = null,
    string? Title = null,
    IReadOnlyList<AstraBlockOption>? Options = null,
    IReadOnlyList<AstraBlockItem>? Items = null,
    bool MultiSelect = false,
    bool AllowFreeText = false,
    string? Action = null,
    IReadOnlyList<AstraQuestion>? Questions = null,
    string? Intro = null,
    AstraQuestionProgress? Progress = null);
