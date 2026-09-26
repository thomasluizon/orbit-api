using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

public record RescheduleSuggestion(
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    DateOnly DueDate,
    TimeOnly? DueTime,
    IReadOnlyList<DayOfWeek> Days,
    string Rationale);
