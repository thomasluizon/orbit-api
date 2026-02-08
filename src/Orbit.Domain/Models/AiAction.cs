using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

public record AiAction
{
    public required AiActionType Type { get; init; }
    public Guid? HabitId { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public decimal? Value { get; init; }
    public DateOnly? DueDate { get; init; }
    public Guid? TaskId { get; init; }
    public TaskItemStatus? NewStatus { get; init; }
    public FrequencyUnit? FrequencyUnit { get; init; }
    public int? FrequencyQuantity { get; init; }
    public HabitType? HabitType { get; init; }
    public string? Unit { get; init; }
    public List<System.DayOfWeek>? Days { get; init; }
}
