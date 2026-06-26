using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

/// <summary>
/// An AI-suggested starting point for a new habit: a representative emoji, a recurrence schedule
/// (or null fields for a one-time task), the weekdays it should run on (non-empty only for a daily
/// habit), and a breakdown into concrete sub-habit titles. Every field is optional so the user can
/// accept or edit any part; the shapes map 1:1 onto the create-habit request.
/// </summary>
public record HabitSetupSuggestion(
    string? Emoji,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    IReadOnlyList<DayOfWeek> Days,
    IReadOnlyList<string> SubHabits);
