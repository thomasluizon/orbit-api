using Orbit.Application.Common.Attributes;

namespace Orbit.Application.Chat;

/// <summary>
/// Metadata-only class for the SuggestBreakdown AI action.
/// This action has no MediatR command -- it passes through as a suggestion in ProcessUserChatCommand.
/// The [AiAction] and [AiField] attributes are discovered by ActionDiscoveryService for prompt generation.
/// </summary>
[AiAction(
    "SuggestBreakdown",
    """**Suggest habit breakdowns** for complex goals (e.g., "help me get fit" -> suggests Exercise parent with Running, Stretching, Gym sub-habits)""",
    """
    - User asks to "break down", "decompose", "help me plan", or asks for suggestions for a complex goal
    - You want to PROPOSE a habit based on something the user mentioned casually (e.g., "I like gaming" -> suggest a weekly gaming habit)
    - User is vague and you want to offer options before committing
    - SuggestBreakdown works for SINGLE habits too -- just put one item in suggestedSubHabits. The user gets accept/decline/edit buttons.
    - SuggestBreakdown NEVER creates anything - it only proposes. The user must confirm before creation.
    """,
    DisplayOrder = 15)]
[AiExample(
    "Help me get fit",
    """{ "actions": [{ "type": "SuggestBreakdown", "title": "Get Fit", "frequencyUnit": "Day", "frequencyQuantity": 1, "dueDate": "{TODAY}", "suggestedSubHabits": [{ "type": "CreateHabit", "title": "Morning Run", "description": "30min jog", "frequencyUnit": "Day", "frequencyQuantity": 1, "dueDate": "{TODAY}" }, { "type": "CreateHabit", "title": "Stretching", "frequencyUnit": "Day", "frequencyQuantity": 1, "dueDate": "{TODAY}" }] }], "aiMessage": "Here's a plan to get fit! Review and let me know what you think." }""")]
public class SuggestBreakdownAction
{
    [AiField("string", "Parent habit name", Required = true)]
    public string Title { get; init; } = default!;

    [AiField("string", "Optional description")]
    public string? Description { get; init; }

    [AiField("Day|Week|Month|Year", "Frequency unit")]
    public string? FrequencyUnit { get; init; }

    [AiField("integer", "Frequency quantity")]
    public int? FrequencyQuantity { get; init; }

    [AiField("string", "YYYY-MM-DD due date")]
    public string? DueDate { get; init; }

    [AiField("object[]", "Array of habit objects with type: \"CreateHabit\", title, description, frequencyUnit, frequencyQuantity, dueDate")]
    public object? SuggestedSubHabits { get; init; }
}
