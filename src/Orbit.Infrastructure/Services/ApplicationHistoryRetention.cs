using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Services;

internal static class ApplicationHistoryRetention
{
    internal static async Task<int> PurgeAsync(OrbitDbContext db, IConfiguration configuration, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var nowAtUtc = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(nowAtUtc);
        var deleted = 0;
        var batchSize = configuration.GetValue("BackgroundServices:CleanupBatchSize", BatchedDelete.DefaultBatchSize);
        async Task Purge<T>(string name, int defaultDays, Func<int, IQueryable<T>> query) where T : Orbit.Domain.Common.Entity
        {
            var days = configuration.GetValue($"BackgroundServices:Retention:{name}Days", defaultDays);
            ArgumentOutOfRangeException.ThrowIfNegative(days);
            if (days > 0) deleted += await BatchedDelete.ExecuteAsync(query(days), cancellationToken, batchSize);
        }
        await Purge("Notifications", 90, days => db.Notifications.Where(row => row.CreatedAtUtc < nowAtUtc.AddDays(-days)));
        var earliestUserToday = today.AddDays(-1);
        await Purge("AiBudgetCalls", 8, days => db.AiBudgetCalls.Where(row => row.LocalDate < today.AddDays(-days)
            && !db.Users.IgnoreQueryFilters().Any(user => user.Id == row.UserId && user.AiMessagesWeekStart.HasValue
                && row.LocalDate >= user.AiMessagesWeekStart.Value
                && row.LocalDate < user.AiMessagesWeekStart.Value.AddDays(AiAllowancePeriod.MaximumProPeriodDays)
                && user.AiMessagesWeekStart.Value.AddDays(AiAllowancePeriod.MaximumProPeriodDays) > earliestUserToday)));
        await Purge("PendingAgentOperations", 7, days => db.PendingAgentOperations.Where(row => row.ExpiresAtUtc < nowAtUtc.AddDays(-days)));
        await Purge("Sessions", 30, days => db.UserSessionRefreshTokens.Where(row => db.UserSessions.Any(session => session.Id == row.UserSessionId
            && (session.ExpiresAtUtc < nowAtUtc.AddDays(-days) || session.RevokedAtUtc < nowAtUtc.AddDays(-days)))));
        await Purge("Sessions", 30, days => db.UserSessions.Where(row => row.ExpiresAtUtc < nowAtUtc.AddDays(-days) || row.RevokedAtUtc < nowAtUtc.AddDays(-days)));
        await Purge("IssuedAstraBlocks", 30, days => db.IssuedAstraBlocks.Where(row => row.ExpiresAtUtc < nowAtUtc.AddDays(-days)));
        await Purge("PendingClarifications", 7, days => db.PendingClarifications.Where(row => row.ExpiresAtUtc < nowAtUtc.AddDays(-days)));
        await Purge("AgentAuditLogs", 365, days => db.AgentAuditLogs.Where(row => row.CreatedAtUtc < nowAtUtc.AddDays(-days)));
        await Purge("AiUsageDaily", 400, days => db.AiUsageDaily.Where(row => row.Date < today.AddDays(-days)));
        await Purge("DesktopHandoffs", 1, days => db.DesktopHandoffs.Where(row => row.ExpiresAtUtc < nowAtUtc.AddDays(-days)));
        await Purge("ProactiveItems", 30, days => db.ProactiveItems.Where(row => row.LocalDate < today.AddDays(-days)
            && row.EndedAtUtc != null && db.ProactiveItems.Count(newer => newer.UserId == row.UserId && newer.Trigger == row.Trigger
                && newer.EndedAtUtc != null && (newer.EndedAtUtc > row.EndedAtUtc
                    || (newer.EndedAtUtc == row.EndedAtUtc && (newer.CreatedAtUtc > row.CreatedAtUtc || (newer.CreatedAtUtc == row.CreatedAtUtc && newer.Id.CompareTo(row.Id) > 0))))) >= Orbit.Domain.Models.ProactiveRules.IgnoredItemsBeforeRest));
        await Purge("ProactiveCalendar", 30, days => db.ProactiveCalendarDays.Where(row => row.LocalDate < today.AddDays(-days)));
        deleted += await BatchedDelete.ExecuteAsync(db.OAuthStates.Where(row => row.ExpiresAtUtc <= nowAtUtc), cancellationToken, batchSize);
        return deleted;
    }
}
