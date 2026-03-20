namespace Orbit.Domain.Models;

public record SlipPattern(
    Guid HabitId,
    DayOfWeek DayOfWeek,
    int? PeakHour,
    int OccurrenceCount,
    double Confidence);
