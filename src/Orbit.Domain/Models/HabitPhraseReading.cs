using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

/// <summary>
/// The result of reading a free-form habit phrase. <see cref="Read"/> is true when the AI produced
/// the fields; when it is false every value is null or empty and the client asks the questions itself.
/// </summary>
public record HabitPhraseReading(
    bool Read,
    string? Emoji,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<DayOfWeek> Days,
    bool IsFlexible,
    int? FlexibleTarget,
    string? DueTime,
    IReadOnlyList<string> SubHabits,
    IReadOnlyList<string> ChecklistItems)
{
    public static HabitPhraseReading Unread { get; } = new(
        false, null, null, null, [], false, null, null, [], []);

    public static HabitPhraseReading From(HabitSetupSuggestion suggestion) => new(
        true,
        suggestion.Emoji,
        suggestion.FrequencyUnit,
        suggestion.FrequencyQuantity,
        suggestion.Days,
        suggestion.IsFlexible,
        suggestion.FlexibleTarget,
        suggestion.DueTime,
        suggestion.SubHabits,
        suggestion.ChecklistItems);
}
