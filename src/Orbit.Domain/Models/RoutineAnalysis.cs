namespace Orbit.Domain.Models;

public record RoutinePattern
{
    public required Guid HabitId { get; init; }
    public required string HabitTitle { get; init; }
    public required string Description { get; init; }  // "user typically logs this Mon/Wed/Fri around 7:00 AM"
    public required decimal ConsistencyScore { get; init; }  // 0.0 - 1.0
    public required string Confidence { get; init; }  // "HIGH" | "MEDIUM" | "LOW"
    public required IReadOnlyList<TimeBlock> TimeBlocks { get; init; }
}

public record TimeBlock
{
    public required DayOfWeek DayOfWeek { get; init; }
    public required int StartHour { get; init; }  // 0-23 (user's local timezone)
    public required int EndHour { get; init; }
}
