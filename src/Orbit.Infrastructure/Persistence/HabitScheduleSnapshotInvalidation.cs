using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

public static class HabitScheduleSnapshotInvalidation
{
    public static void Attach(OrbitDbContext context, HabitScheduleSnapshotStore snapshots)
    {
        var hasHabitChanges = false;
        context.SavingChanges += (_, _) =>
        {
            hasHabitChanges = context.ChangeTracker.Entries<Habit>()
                .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        };
        context.SavedChanges += (_, _) =>
        {
            if (hasHabitChanges)
                snapshots.Invalidate();
            hasHabitChanges = false;
        };
        context.SaveChangesFailed += (_, _) =>
        {
            snapshots.Invalidate();
            hasHabitChanges = false;
        };
    }
}
