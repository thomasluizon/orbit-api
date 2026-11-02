using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Persistence;

internal static class AiUsageSummaryReader
{
    internal sealed class QuotaSummary
    {
        public long ActiveUsers { get; set; }
        public long FreeCapHits { get; set; }
        public long ProCapHits { get; set; }
        public double Mean { get; set; }
        public int P95 { get; set; }
    }

    internal static async Task<List<AiUsageDaily>> ReadUsageAsync(OrbitDbContext db, DateOnly date, CancellationToken ct)
    {
        var groups = await db.AiUsageDaily.AsNoTracking().Where(row => row.Date == date)
            .GroupBy(row => new { row.Model, row.Purpose })
            .Select(group => new
            {
                group.Key.Model,
                group.Key.Purpose,
                Calls = group.Sum(row => row.Calls),
                Cached = group.Sum(row => row.CachedTokens),
                Prompt = group.Sum(row => row.PromptTokens),
                Completion = group.Sum(row => row.CompletionTokens),
                Total = group.Sum(row => row.TotalTokens),
                Reasoning = group.Sum(row => row.ReasoningTokens),
                Cost = group.Sum(row => row.CostUsd)
            }).ToListAsync(ct);
        return groups.Select(row => AiUsageDaily.Create(date, row.Model, row.Purpose,
            new(row.Calls, row.Cached, row.Prompt, row.Completion, row.Total, row.Cost, row.Reasoning))).ToList();
    }

    internal static IQueryable<QuotaSummary> BuildPostgresQuotaQuery(OrbitDbContext db, DateOnly date, DateTime nowAtUtc,
        int freeLimit, int proLimit) =>
        db.Database.SqlQuery<QuotaSummary>($"""
                WITH "Active" AS (
                    SELECT "AiMessagesUsedToday" AS "Messages", "AiMessagesUsedThisWeek" AS "Weekly",
                        "AiMessagesWeekStart" AS "StoredStart",
                        ("IsLifetimePro" OR "BonusProExpiresAtUtc" > {nowAtUtc}
                            OR ("Plan" = {(int)UserPlan.Pro} AND "PlanExpiresAt" > {nowAtUtc})
                            OR "TrialEndsAt" > {nowAtUtc}) IS TRUE AS "Pro",
                        CASE WHEN "AiMessagesWeekStart" IS NOT NULL
                            AND {date} < "AiMessagesWeekStart" + (7 + ("WeekStartDay" - EXTRACT(DOW FROM "AiMessagesWeekStart")::int + 7) % 7)
                            THEN "AiMessagesWeekStart"
                            ELSE {date} - ((EXTRACT(DOW FROM {date})::int - "WeekStartDay" + 7) % 7) END AS "Start"
                    FROM "Users" WHERE "IsDeactivated" = FALSE
                        AND "AiMessagesLocalDate" = {date} AND "AiMessagesUsedToday" > 0
                )
                SELECT COUNT(*) AS "ActiveUsers",
                    COUNT(*) FILTER (WHERE NOT "Pro" AND "Messages" >= {freeLimit}) AS "FreeCapHits",
                    COUNT(*) FILTER (WHERE "Pro" AND "StoredStart" >= "Start" AND "Weekly" >= {proLimit}) AS "ProCapHits",
                    COALESCE(AVG("Messages"), 0)::double precision AS "Mean",
                    COALESCE(percentile_disc(0.95) WITHIN GROUP (ORDER BY "Messages"), 0) AS "P95"
                FROM "Active"
                """);

    internal static async Task<QuotaSummary> ReadQuotaAsync(OrbitDbContext db, DateOnly date, DateTime nowAtUtc,
        int freeLimit, int proLimit, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            return await BuildPostgresQuotaQuery(db, date, nowAtUtc, freeLimit, proLimit).SingleAsync(ct);

        var grouped = await db.Users.AsNoTracking().Where(user => user.AiMessagesLocalDate == date && user.AiMessagesUsedToday > 0)
            .Select(user => new
            {
                Messages = user.AiMessagesUsedToday,
                Pro = user.IsLifetimePro || user.BonusProExpiresAtUtc > nowAtUtc
                    || (user.SubscriptionPlan == UserPlan.Pro && user.SubscriptionExpiresAtUtc > nowAtUtc) || user.TrialEndsAt > nowAtUtc,
                WeeklyHit = user.AiMessagesUsedThisWeek >= proLimit && user.AiMessagesWeekStart >=
                    (user.AiMessagesWeekStart.HasValue && date < user.AiMessagesWeekStart.Value.AddDays(
                        7 + (user.WeekStartDay - (int)user.AiMessagesWeekStart.Value.DayOfWeek + 7) % 7)
                        ? user.AiMessagesWeekStart.Value : date.AddDays(-((int)date.DayOfWeek - user.WeekStartDay + 7) % 7))
            })
            .GroupBy(row => new { row.Messages, row.Pro, row.WeeklyHit })
            .Select(group => new { group.Key.Messages, group.Key.Pro, group.Key.WeeklyHit, Count = group.LongCount() })
            .ToListAsync(ct);
        var count = grouped.Sum(row => row.Count);
        if (count == 0) return new();
        var rank = (long)Math.Ceiling(count * .95);
        var cumulative = 0L;
        var p95 = 0;
        foreach (var row in grouped.OrderBy(row => row.Messages))
        {
            cumulative += row.Count;
            if (cumulative < rank) continue;
            p95 = row.Messages;
            break;
        }
        return new()
        {
            ActiveUsers = count,
            FreeCapHits = grouped.Where(row => !row.Pro && row.Messages >= freeLimit).Sum(row => row.Count),
            ProCapHits = grouped.Where(row => row.Pro && row.WeeklyHit).Sum(row => row.Count),
            Mean = grouped.Sum(row => (double)row.Messages * row.Count) / count,
            P95 = p95
        };
    }
}
