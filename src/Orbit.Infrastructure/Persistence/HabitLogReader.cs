using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public class HabitLogReader(OrbitDbContext context) : IHabitLogReader
{
    public Task<DateOnly?> GetLastCompletionDateAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return context.Habits
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(habit => habit.UserId == userId && !habit.IsBadHabit)
            .SelectMany(habit => context.HabitLogs
                .Where(log => log.HabitId == habit.Id && log.Value > 0 && !log.IsDeleted))
            .MaxAsync(log => (DateOnly?)log.Date, cancellationToken);
    }

    public async Task<IReadOnlyList<HabitLog>> ReadRecentLogsAsync(
        Guid habitId,
        DateOnly since,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await BuildRecentLogs(context.HabitLogs.AsNoTracking(), habitId, since, limit)
            .ToListAsync(cancellationToken);
    }

    internal static IQueryable<HabitLog> BuildRecentLogs(
        IQueryable<HabitLog> logs,
        Guid habitId,
        DateOnly since,
        int limit)
    {
        return logs
            .Where(l => l.HabitId == habitId && l.Date >= since)
            .OrderByDescending(l => l.Date)
            .ThenByDescending(l => l.CreatedAtUtc)
            .Take(limit);
    }
}
