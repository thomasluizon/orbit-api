using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Events;

namespace Orbit.Infrastructure.Persistence;

public class AccountResetRepository(OrbitDbContext context, IAccountEventCollector eventCollector) : IAccountResetRepository
{
    public async Task DeleteAllUserDataAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await context.ChatTurns.Where(turn => turn.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await context.ConversationTurns.Where(turn => turn.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await context.NotionTasks.Where(t => t.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await context.NotionConnections.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await context.NotionOAuthStates.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await context.DesktopHandoffs.Where(handoff => handoff.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        var habitIds = await context.Habits
            .IgnoreQueryFilters()
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
                .IgnoreQueryFilters()
                .Where(l => habitIds.Contains(l.HabitId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        var goalIds = await context.Goals
            .IgnoreQueryFilters()
            .Where(g => g.UserId == userId)
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);

        if (goalIds.Count > 0)
        {
            await context.GoalProgressLogs
                .IgnoreQueryFilters()
                .Where(l => goalIds.Contains(l.GoalId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        await context.Notifications
            .IgnoreQueryFilters()
            .Where(n => n.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.PushSubscriptions
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.UserAchievements
            .Where(ua => ua.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.XpAwardLogs
            .Where(award => award.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.StreakFreezes
            .Where(sf => sf.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.ProactiveItems
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.ProactiveCalendarDays
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.ApiKeys
            .Where(k => k.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Tags
            .IgnoreQueryFilters()
            .Where(t => t.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.ChecklistTemplates
            .IgnoreQueryFilters()
            .Where(ct => ct.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.GoogleCalendarSyncSuggestions
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.AgentStepUpChallenges
            .Where(c => c.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.PendingAgentOperations
            .Where(o => o.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.AgentAuditLogs
            .Where(a => a.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.IssuedAstraBlocks
            .Where(block => block.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.PendingClarifications
            .Where(pc => pc.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.ProcessedRequests
            .Where(r => r.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.ClosedMonthRecaps
            .Where(recap => recap.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Referrals
            .Where(r => r.ReferrerId == userId || r.ReferredUserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Goals
            .IgnoreQueryFilters()
            .Where(g => g.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Habits
            .IgnoreQueryFilters()
            .Where(h => h.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        eventCollector.MarkResync(userId);
    }
}
