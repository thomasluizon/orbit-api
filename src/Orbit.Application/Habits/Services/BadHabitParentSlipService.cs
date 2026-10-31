using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Services;

internal static class BadHabitParentSlipService
{
    public static async Task<Result<HabitLog?>> LogAsync(
        Habit child, DateOnly date, DateOnly today, int weekStartDay,
        IGenericRepository<Habit> habits, CancellationToken cancellationToken)
    {
        var parent = await LoadParentAsync(child, date, habits, cancellationToken);
        if (parent is null || parent.Logs.Any(log => IsSlipOnDate(log, date))
            || !await AllChildrenSlippedAsync(parent, date, habits, cancellationToken))
            return Result.Success<HabitLog?>(null);

        var logged = parent.Log(date, advanceDueDate: date >= today,
            weekStartDay: weekStartDay, loggedOnLocalDate: today);
        return logged.IsSuccess
            ? Result.Success<HabitLog?>(logged.Value)
            : new Result<HabitLog?>(null, false, logged.Error, logged.ErrorCode, logged.ErrorArgs);
    }

    public static async Task RemoveIfIncompleteAsync(
        Habit child, DateOnly date, IGenericRepository<Habit> habits, CancellationToken cancellationToken)
    {
        var parent = await LoadParentAsync(child, date, habits, cancellationToken);
        if (parent is null || await AllChildrenSlippedAsync(parent, date, habits, cancellationToken))
            return;

        while (parent.Logs.Any(log => IsSlipOnDate(log, date)))
            parent.Unlog(date);
    }

    private static Task<Habit?> LoadParentAsync(
        Habit child, DateOnly date, IGenericRepository<Habit> habits, CancellationToken cancellationToken) =>
        !child.IsBadHabit || child.ParentHabitId is null
            ? Task.FromResult<Habit?>(null)
            : habits.FindOneTrackedAsync(
                habit => habit.Id == child.ParentHabitId && habit.UserId == child.UserId
                    && habit.IsBadHabit && !habit.IsDeleted,
                query => query.Include(habit => habit.Logs.Where(log => log.Date == date)),
                cancellationToken);

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
