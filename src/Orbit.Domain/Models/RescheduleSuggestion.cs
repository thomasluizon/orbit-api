using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

/// <summary>
/// An AI-proposed, already-validated reschedule for a missed (overdue) habit. The schedule fields
/// are clamped to a valid combination at the service boundary — <see cref="DueDate"/> on or after
/// the user's today, a positive <see cref="FrequencyQuantity"/> whenever <see cref="FrequencyUnit"/>
/// is set, and <see cref="Days"/> only for a daily, quantity-one cadence — so a client can apply it
/// verbatim through the existing habit-update path.
/// </summary>
public record RescheduleSuggestion(
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    DateOnly DueDate,
    TimeOnly? DueTime,
    IReadOnlyList<DayOfWeek> Days,
    string Rationale);
