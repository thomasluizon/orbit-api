using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
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
        while (await LoadParentAsync(child, date, habits, cancellationToken) is { } parent && visited.Add(parent.Id))
        {
            if (!await AllChildrenSlippedAsync(parent, date, habits, cancellationToken))
                break;
            if (!parent.Logs.Any(log => IsSlipOnDate(log, date)))
            {
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

    public static async Task RemoveIfIncompleteAsync(
        Habit child, DateOnly date, IGenericRepository<Habit> habits, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid> { child.Id };
        while (await LoadParentAsync(child, date, habits, cancellationToken) is { } parent && visited.Add(parent.Id))
        {
            if (await AllChildrenSlippedAsync(parent, date, habits, cancellationToken))
                break;
            while (parent.Logs.Any(log => IsSlipOnDate(log, date)))
                parent.Unlog(date);
            child = parent;
        }
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
