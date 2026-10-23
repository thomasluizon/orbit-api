using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
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
            habit.StartDate));

    public static IQueryable<UserHabitScheduleSnapshot> SelectForFreeze(IQueryable<Habit> query) =>
        query.Select(habit => new UserHabitScheduleSnapshot(
            habit.UserId,
            new HabitScheduleSnapshot(
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
                habit.StartDate)));
}

public sealed record UserHabitScheduleSnapshot(Guid UserId, HabitScheduleSnapshot Snapshot);
