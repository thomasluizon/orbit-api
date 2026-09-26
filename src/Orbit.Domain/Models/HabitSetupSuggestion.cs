using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

public record HabitSetupSuggestion(
    string? Emoji,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<DayOfWeek> Days,
    bool IsFlexible,
    int? FlexibleTarget,
    string? DueTime,
    IReadOnlyList<string> SubHabits,
    IReadOnlyList<string> ChecklistItems);
