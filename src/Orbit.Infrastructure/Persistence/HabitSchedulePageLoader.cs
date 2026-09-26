using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class HabitSchedulePageLoader(OrbitDbContext context) : IHabitSchedulePageLoader
{
    public async Task LoadAsync(
        IReadOnlyCollection<Orbit.Domain.Entities.Habit> habits,
        DateOnly logFrom,
        DateOnly logTo,
        CancellationToken cancellationToken = default)
    {
        if (habits.Count == 0)
            return;

        var ids = habits.Select(habit => habit.Id).ToArray();
        var snapshots = await context.HabitLogs.AsNoTracking()
            .TagWith("HabitSchedulePageLogs")
            .Where(log => ids.Contains(log.HabitId) && log.Date >= logFrom && log.Date <= logTo)
            .Select(log => new
            {
                log.Id,
                log.HabitId,
                log.Date,
                log.Value,
                log.CompletionOrdinal,
                log.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var logsByHabit = snapshots
            .Select(log => Orbit.Domain.Entities.HabitLog.FromScheduleRead(
                log.Id, log.HabitId, log.Date, log.Value, log.CompletionOrdinal, log.CreatedAtUtc))
            .ToLookup(log => log.HabitId);
        foreach (var habit in habits)
            habit.LoadScheduleLogsForRead(logsByHabit[habit.Id]);

        var relations = await context.Habits.AsNoTracking()
            .Where(habit => ids.Contains(habit.Id))
            .Select(habit => new { habit.Id, Tags = habit.Tags.ToList(), Goals = habit.Goals.ToList() })
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var byId = habits.ToDictionary(habit => habit.Id);
        foreach (var relation in relations)
            byId[relation.Id].LoadScheduleRelationsForRead(relation.Tags, relation.Goals);
    }
}
