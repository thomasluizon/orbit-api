using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public class AccountResetRepository(OrbitDbContext context) : IAccountResetRepository
{
    public async Task DeleteAllUserDataAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var habitIds = await context.Habits
            .Where(h => h.UserId == userId)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken);

        if (habitIds.Count > 0)
        {
            await context.SentReminders
                .Where(r => habitIds.Contains(r.HabitId))
                .ExecuteDeleteAsync(cancellationToken);

            await context.SentSlipAlerts
                .Where(a => habitIds.Contains(a.HabitId))
                .ExecuteDeleteAsync(cancellationToken);

            await context.HabitLogs
                .Where(l => habitIds.Contains(l.HabitId))
                .ExecuteDeleteAsync(cancellationToken);
        }

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

        await context.SentStreakFreezeAlerts
            .Where(a => a.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.ApiKeys
            .Where(k => k.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.UserFacts
            .IgnoreQueryFilters()
            .Where(f => f.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Tags
            .Where(t => t.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Goals
            .Where(g => g.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Habits
            .Where(h => h.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
