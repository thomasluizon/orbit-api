using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

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
    public bool? SlipAlertEnabled { get; init; }
    public bool? ReminderEnabled { get; init; }
    public int? ReminderMinutesBefore { get; init; }
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    public string? Note { get; init; }
    public List<AiAction>? SubHabits { get; init; }
    public List<AiAction>? SuggestedSubHabits { get; init; }
    public List<string>? TagNames { get; init; }
    public List<ChecklistItem>? ChecklistItems { get; init; }
}
