using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

/// <summary>
/// Loads the owner's habits for a bulk operation, keyed by id, each with its logs inside the
/// overdue window and flexible periods so processors can evaluate scheduling and prior completions.
/// </summary>
internal static class BulkHabitLoader
{
    public static async Task<Dictionary<Guid, Habit>> LoadHabitsWithRecentLogsAsync(
        IGenericRepository<Habit> habitRepository,
        IGenericRepository<HabitLog> habitLogRepository,
        IEnumerable<Guid> habitIds,
        Guid userId,
        DateOnly today,
        int weekStartDay,
        CancellationToken cancellationToken,
        bool includeGoals = true)
    {
        var idSet = habitIds.ToHashSet();
        var habits = await habitRepository.FindTrackedAsync(
            h => idSet.Contains(h.Id) && h.UserId == userId,
            q => includeGoals ? q.Include(h => h.Goals).AsSplitQuery() : q,
            cancellationToken);
        var windowStart = today.AddDays(-AppConstants.DefaultOverdueWindowDays);
        var flexibleHabits = habits.Where(habit => habit.Schedule == HabitSchedule.Flexible).ToArray();
        var flexibleIds = flexibleHabits.Select(habit => habit.Id).ToHashSet();
        var periodStart = flexibleHabits.Select(habit => HabitScheduleService.GetWindowStart(habit, windowStart, weekStartDay))
            .DefaultIfEmpty(windowStart).Min();
        var loadedIds = habits.Select(habit => habit.Id).ToHashSet();
        await habitLogRepository.FindTrackedAsync(log => loadedIds.Contains(log.HabitId)
            && (log.Date >= windowStart || flexibleIds.Contains(log.HabitId) && log.Date >= periodStart), cancellationToken);
        return habits.ToDictionary(h => h.Id);
    }
}
