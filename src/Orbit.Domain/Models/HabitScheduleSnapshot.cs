using Orbit.Domain.Enums;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Models;

public sealed record HabitScheduleSnapshot(
    Guid Id,
    Guid? ParentHabitId,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    int? IntervalWeeks,
    DateOnly DueDate,
    DateOnly? ScheduledStartDate,
    int? OriginalDayOfMonth,
    DateOnly? EndDate,
    DateTime CreatedAtUtc,
    DateTime? DeletedAtUtc,
    bool IsDeleted,
    bool IsBadHabit,
    bool IsCompleted,
    bool IsGeneral,
    bool IsFlexible,
    IReadOnlyList<DayOfWeek> Days)
{
    public static HabitScheduleSnapshot FromHabit(Habit habit) => new(
        habit.Id,
        habit.ParentHabitId,
        habit.FrequencyUnit,
        habit.FrequencyQuantity,
        habit.IntervalWeeks,
        habit.DueDate,
        habit.ScheduledStartDate,
        habit.OriginalDayOfMonth,
        habit.EndDate,
        habit.CreatedAtUtc,
        habit.DeletedAtUtc,
        habit.IsDeleted,
        habit.IsBadHabit,
        habit.IsCompleted,
        habit.IsGeneral,
        habit.IsFlexible,
        habit.Days.ToList());
}
