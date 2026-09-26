using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Services;

public static class HabitScheduleProjection
{
    public static IQueryable<HabitScheduleSnapshot> Select(IQueryable<Habit> query) =>
        query.Select(habit => new HabitScheduleSnapshot(
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
            habit.Days.ToList()));
}
