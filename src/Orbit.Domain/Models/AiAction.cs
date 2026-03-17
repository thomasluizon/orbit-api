using Orbit.Domain.Enums;

namespace Orbit.Domain.Models;

public record AiAction
{
    public AiActionType Type { get; init; } = AiActionType.CreateHabit;
    public Guid? HabitId { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public FrequencyUnit? FrequencyUnit { get; init; }
    public int? FrequencyQuantity { get; init; }
    public List<System.DayOfWeek>? Days { get; init; }
    public bool? IsBadHabit { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? Note { get; init; }
    public List<AiAction>? SubHabits { get; init; }
    public List<AiAction>? SuggestedSubHabits { get; init; }
}
