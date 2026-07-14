using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Commands;

/// <summary>
/// Loads the owner's habits for a bulk operation, keyed by id, each with its logs inside the
/// overdue window so the per-item processors can evaluate scheduling and prior completions.
/// </summary>
internal static class BulkHabitLoader
{
    public static async Task<Dictionary<Guid, Habit>> LoadHabitsWithRecentLogsAsync(
        IGenericRepository<Habit> habitRepository,
        IEnumerable<Guid> habitIds,
        Guid userId,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var idSet = habitIds.ToHashSet();
        var windowStart = today.AddDays(-AppConstants.DefaultOverdueWindowDays);
        var habits = await habitRepository.FindTrackedAsync(
            h => idSet.Contains(h.Id) && h.UserId == userId,
            q => q.Include(h => h.Logs.Where(l => l.Date >= windowStart)),
            cancellationToken);
        return habits.ToDictionary(h => h.Id);
    }
}
