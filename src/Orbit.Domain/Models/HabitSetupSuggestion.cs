using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

/// <summary>
/// An AI-suggested starting point for a new habit, shaped to map 1:1 onto the create-habit request so
/// the user can accept or edit any part: a representative emoji; a recurrence (null
/// <see cref="FrequencyUnit"/>/<see cref="FrequencyQuantity"/> for a one-time task); the fixed weekdays
/// it runs on (non-empty only for a daily, quantity-one cadence); a flexible "N times per period" goal
/// (<see cref="IsFlexible"/> with the per-period count in <see cref="FlexibleTarget"/>, mutually
/// exclusive with <see cref="Days"/>); an optional 24-hour "HH:mm" <see cref="DueTime"/>; and a
/// breakdown that is EITHER separately-trackable <see cref="SubHabits"/> OR tick-off
/// <see cref="ChecklistItems"/> on the one habit, never both.
/// </summary>
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
