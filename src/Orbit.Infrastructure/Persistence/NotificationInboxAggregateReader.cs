using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class NotificationInboxAggregateReader(OrbitDbContext context) : INotificationInboxAggregateReader
{
    public async Task<NotificationInboxAggregate> ReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!context.Database.IsRelational())
        {
            var rows = await context.Notifications.AsNoTracking().Where(n => n.UserId == userId
                && (n.Url == null || (!n.Url.StartsWith("/social") && !n.Url.StartsWith("/public-profile") && !n.Url.StartsWith("/u/"))))
                .OrderBy(n => n.Id).Select(n => new { n.Id, n.UpdatedAtUtc, n.IsRead }).ToListAsync(cancellationToken);
            return new(rows.Count(n => !n.IsRead), rows.Count, ReadRevision.Hash(rows));
        }
        var rowsSql = await context.Database.SqlQuery<AggregateSqlRow>($"""
            SELECT CAST(COUNT(*) AS integer) AS "TotalCount",
                CAST(COALESCE(SUM(CASE WHEN NOT "IsRead" THEN 1 ELSE 0 END), 0) AS integer) AS "UnreadCount",
                md5(COALESCE(string_agg(CAST("Id" AS text) || ':' || CAST("UpdatedAtUtc" AS text) || ':' || CAST("IsRead" AS text),
                    ',' ORDER BY "Id"), '')) AS "Revision"
            FROM "Notifications"
            WHERE "UserId" = {userId} AND NOT "IsDeleted"
                AND ("Url" IS NULL OR ("Url" NOT LIKE '/social%' AND "Url" NOT LIKE '/public-profile%' AND "Url" NOT LIKE '/u/%'))
            """).ToListAsync(cancellationToken);
        var row = rowsSql.Single();
        return new(row.UnreadCount, row.TotalCount, row.Revision);
    }

    private sealed class AggregateSqlRow
    {
        public int UnreadCount { get; set; }
        public int TotalCount { get; set; }
        public string Revision { get; set; } = string.Empty;
    }
}
