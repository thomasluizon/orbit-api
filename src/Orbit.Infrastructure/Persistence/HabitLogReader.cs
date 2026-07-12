using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public class HabitLogReader(OrbitDbContext context) : IHabitLogReader
{
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
