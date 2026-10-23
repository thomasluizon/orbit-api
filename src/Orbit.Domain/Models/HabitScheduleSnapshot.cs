using Orbit.Domain.Enums;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Models;

public sealed record HabitScheduleSnapshot(
    Guid Id,
    Guid? ParentHabitId,
    FrequencyUnit? FrequencyUnit,
    int? FrequencyQuantity,
    int? IntervalWeeks,
    DateOnly NextDueDate,
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
    IReadOnlyList<DayOfWeek> Days,
    HabitSchedule Schedule,
    DateOnly StartDate)
{
    public static HabitScheduleSnapshot FromHabit(Habit habit) => new(
        habit.Id,
        habit.ParentHabitId,
        habit.FrequencyUnit,
        habit.FrequencyQuantity,
        habit.IntervalWeeks,
        habit.NextDueDate,
        habit.ScheduledStartDate,
        habit.OriginalDayOfMonth,
        habit.EndDate,
        habit.CreatedAtUtc,
        habit.DeletedAtUtc,
        habit.IsDeleted,
        habit.IsBadHabit,
        habit.IsCompleted,
        habit.Schedule == HabitSchedule.General,
        habit.Schedule == HabitSchedule.Flexible,
        habit.Days.ToList(),
        habit.Schedule,
        habit.StartDate);
}
