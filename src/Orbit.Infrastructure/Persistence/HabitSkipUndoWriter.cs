using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class HabitSkipUndoWriter(OrbitDbContext context, HabitScheduleSnapshotStore? snapshots = null)
    : IHabitSkipUndoWriter
{
    public async Task SaveAsync(Habit habit, DateTime expectedUpdatedAtUtc, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            var current = await context.Entry(habit).GetDatabaseValuesAsync(cancellationToken);
            if (current is null || current.GetValue<DateTime>(nameof(Habit.UpdatedAtUtc)).Ticks / 10
                != expectedUpdatedAtUtc.Ticks / 10)
                throw new DbUpdateConcurrencyException("Habit changed before its skip could be undone.");
            return;
        }

        var updated = await context.Habits.Where(h => h.Id == habit.Id && h.UserId == habit.UserId
                && h.UpdatedAtUtc == expectedUpdatedAtUtc)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(h => h.DueDate, habit.DueDate)
                .SetProperty(h => h.ScheduledStartDate, habit.ScheduledStartDate)
                .SetProperty(h => h.IsCompleted, habit.IsCompleted)
                .SetProperty(h => h.UpdatedAtUtc, habit.UpdatedAtUtc)
                .SetProperty(h => h.ReminderProbeVersion, h => h.ReminderProbeVersion + 1), cancellationToken);
        if (updated != 1)
            throw new DbUpdateConcurrencyException("Habit changed before its skip could be undone.");

        await context.Entry(habit).ReloadAsync(cancellationToken);
        snapshots?.Invalidate();
    }
}
