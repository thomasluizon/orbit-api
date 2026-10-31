using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Services;

internal static class BadHabitParentSlipService
{
    public static async Task<Result<IReadOnlyList<HabitLog>>> LogAsync(
        Habit child, DateOnly date, DateOnly today, int weekStartDay,
        IGenericRepository<Habit> habits, CancellationToken cancellationToken)
    {
        var logs = new List<HabitLog>();
        var visited = new HashSet<Guid> { child.Id };
        while (await LoadParentAsync(child, date, habits, cancellationToken, weekStartDay) is { } parent && visited.Add(parent.Id))
        {
            if (!await AllChildrenSlippedAsync(parent, date, habits, cancellationToken))
                break;
            if (!parent.Logs.Any(log => IsSlipOnDate(log, date)))
            {
                if (parent.CheckLog(date, weekStartDay).IsFailure
                    || (parent.Schedule is HabitSchedule.Recurring or HabitSchedule.Flexible
                        && !HabitScheduleService.IsHabitDueOnDate(parent, date, weekStartDay)))
                    break;
                var logged = parent.Log(date, advanceDueDate: date >= today,
                    weekStartDay: weekStartDay, loggedOnLocalDate: today);
                if (logged.IsFailure)
                    return logged.PropagateError<IReadOnlyList<HabitLog>>();
                logs.Add(logged.Value);
            }
            child = parent;
        }
        return Result.Success<IReadOnlyList<HabitLog>>(logs);
    }

    public static async Task<Result> RemoveIfIncompleteAsync(
        Habit child, DateOnly date, IGenericRepository<Habit> habits, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid> { child.Id };
        while (await LoadParentAsync(child, date, habits, cancellationToken) is { } parent && visited.Add(parent.Id))
        {
            if (await AllChildrenSlippedAsync(parent, date, habits, cancellationToken))
                break;
            var slips = parent.Logs.Where(log => IsSlipOnDate(log, date)).ToArray();
            foreach (var slip in slips)
            {
                var unlogged = parent.Unlog(date, slip.Id);
                if (unlogged.IsFailure)
                    return unlogged;
            }
            child = parent;
        }
        return Result.Success();
    }

    private static async Task<Habit?> LoadParentAsync(
        Habit child, DateOnly date, IGenericRepository<Habit> habits, CancellationToken cancellationToken,
        int? weekStartDay = null)
    {
        if (!child.IsBadHabit || child.ParentHabitId is null)
            return null;

        var parent = await habits.FindOneTrackedAsync(
            habit => habit.Id == child.ParentHabitId && habit.UserId == child.UserId
                && habit.IsBadHabit && !habit.IsDeleted,
            query => query.Include(habit => habit.Logs.Where(log => log.Date == date)),
            cancellationToken);
        if (parent?.Schedule != HabitSchedule.Flexible || !weekStartDay.HasValue)
            return parent;

        var windowStart = HabitScheduleService.GetWindowStart(parent, date, weekStartDay.Value);
        var windowEnd = HabitScheduleService.GetWindowEnd(parent, date, weekStartDay.Value);
        return await habits.FindOneTrackedAsync(
            habit => habit.Id == parent.Id && habit.UserId == child.UserId
                && habit.IsBadHabit && !habit.IsDeleted,
            query => query.Include(habit => habit.Logs.Where(log => log.Date >= windowStart && log.Date <= windowEnd)),
            cancellationToken);
    }

    private static async Task<bool> AllChildrenSlippedAsync(
        Habit parent, DateOnly date, IGenericRepository<Habit> habits, CancellationToken cancellationToken)
    {
        var children = await habits.FindTrackedAsync(
            habit => habit.ParentHabitId == parent.Id && habit.UserId == parent.UserId
                && habit.IsBadHabit && !habit.IsDeleted,
            query => query.Include(habit => habit.Logs.Where(log => log.Date == date)),
            cancellationToken);
        return children.Count > 0 && children.All(child => child.Logs.Any(log => IsSlipOnDate(log, date)));
    }

    private static bool IsSlipOnDate(HabitLog log, DateOnly date) =>
        log.Date == date && log.IsSlip && log.Value > 0 && !log.IsDeleted;
}
