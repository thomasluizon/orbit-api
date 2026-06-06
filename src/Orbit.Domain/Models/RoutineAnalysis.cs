namespace Orbit.Domain.Models;

public record RoutinePattern
{
    public required Guid HabitId { get; init; }
    public required string HabitTitle { get; init; }
    public required string Description { get; init; }    public required decimal ConsistencyScore { get; init; }    public required string Confidence { get; init; }    public required IReadOnlyList<TimeBlock> TimeBlocks { get; init; }
}

public record TimeBlock
{
    public required DayOfWeek DayOfWeek { get; init; }
    public required int StartHour { get; init; }    public required int EndHour { get; init; }
}
