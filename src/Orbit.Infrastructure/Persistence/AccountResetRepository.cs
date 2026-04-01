using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public class AccountResetRepository(OrbitDbContext context) : IAccountResetRepository
{
    public async Task DeleteAllUserDataAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Get habit IDs for this user (needed to delete child records that reference HabitId)
        var habitIds = await context.Habits
            .Where(h => h.UserId == userId)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken);

        if (habitIds.Count > 0)
        {
            // Delete records that reference HabitId but have no cascade from Habit
            await context.SentReminders
                .Where(r => habitIds.Contains(r.HabitId))
                .ExecuteDeleteAsync(cancellationToken);

            await context.SentSlipAlerts
                .Where(a => habitIds.Contains(a.HabitId))
                .ExecuteDeleteAsync(cancellationToken);

            // Delete HabitLogs (cascade would handle this, but explicit is faster for bulk)
            await context.HabitLogs
                .Where(l => habitIds.Contains(l.HabitId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        // Get goal IDs for this user (needed to delete GoalProgressLogs)
        var goalIds = await context.Goals
            .Where(g => g.UserId == userId)
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);

        if (goalIds.Count > 0)
        {
            await context.GoalProgressLogs
                .Where(l => goalIds.Contains(l.GoalId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        // Delete join tables (HabitTags, HabitGoals) by deleting the owning entities
        // EF Core cascade handles these when we delete Habits/Goals/Tags

        // Delete entities that have direct UserId
        await context.Notifications
            .Where(n => n.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.PushSubscriptions
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.UserAchievements
            .Where(ua => ua.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.StreakFreezes
            .Where(sf => sf.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.ApiKeys
            .Where(k => k.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        // UserFacts has a query filter (IsDeleted), use IgnoreQueryFilters to delete all
        await context.UserFacts
            .IgnoreQueryFilters()
            .Where(f => f.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        // Delete Tags (cascade deletes HabitTags join table entries)
        await context.Tags
            .Where(t => t.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        // Delete Goals (cascade deletes HabitGoals join table entries + GoalProgressLogs already deleted)
        await context.Goals
            .Where(g => g.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        // Delete Habits (cascade deletes remaining HabitLogs + Children)
        await context.Habits
            .Where(h => h.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
