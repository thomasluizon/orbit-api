using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Models;

public sealed record AskToolArguments(
    string Form,
    string? Question = null,
    string? Title = null,
    IReadOnlyList<AiBlockOption>? Options = null,
    IReadOnlyList<AiBlockItem>? Items = null,
    bool MultiSelect = false,
    bool AllowFreeText = false,
    string? Action = null,
    IReadOnlyList<AiQuestion>? Questions = null,
    string? Intro = null,
    AiQuestionProgress? Progress = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("missing_input")] string? MissingInput = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("proposed_habit_title")] string? ProposedHabitTitle = null);
