using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Persistence;

public sealed class BoundedReadStateBackfill(OrbitDbContext db, IUserDateService dates)
{
    internal const string CompletionKey = "BoundedReadStateBackfillComplete";

    internal IQueryable<Habit> PendingHabits(Guid? cursor) => db.Habits.AsNoTracking()
        .Where(h => h.LongestRecordedStreak == null && (cursor == null || h.Id.CompareTo(cursor.Value) > 0))
        .OrderBy(h => h.Id).Take(100);

    internal IQueryable<User> PendingUsers(Guid? cursor) => db.Users.AsNoTracking()
        .Where(user => EF.Property<bool?>(user, XpHistoryStore.InitializedProperty) != true
            && (cursor == null || user.Id.CompareTo(cursor.Value) > 0))
        .OrderBy(user => user.Id).Take(100);

    public async Task RunAsync(CancellationToken ct = default)
    {
        await BackfillXpAsync(ct);
        if (await db.AppConfigs.AnyAsync(config => config.Key == CompletionKey, ct)) return;
        Guid? cursor = null;
        while (true)
        {
            var batch = await PendingHabits(cursor)
                .Select(h => new { h.Id, h.UserId }).ToArrayAsync(ct);
            if (batch.Length == 0) break;
            var ids = batch.Select(h => h.Id).ToArray();
            var userIds = batch.Select(h => h.UserId).Distinct().ToArray();
            var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.TimeZone, u.WeekStartDay }).ToDictionaryAsync(u => u.Id, ct);
            var snapshots = await HabitScheduleProjection.Select(db.Habits.AsNoTracking().Where(h => ids.Contains(h.Id))).ToArrayAsync(ct);
            var todayByUser = new Dictionary<Guid, DateOnly>();
            foreach (var user in users.Values)
                todayByUser[user.Id] = await dates.GetUserTodayAsync(user.TimeZone, user.Id, ct);
            if (todayByUser.Count == 0) { cursor = batch[^1].Id; continue; }
            var cutoff = todayByUser.Values.Min().AddDays(-AppConstants.StreakLogWindowDays);
            var logs = await db.HabitLogs.AsNoTracking().Where(l => ids.Contains(l.HabitId) && l.Date >= cutoff)
                .Select(l => new HabitMetricLog(l.HabitId, l.Date, l.Value, false)).ToArrayAsync(ct);
            var byHabit = logs.ToLookup(l => l.HabitId);
            var owners = batch.ToDictionary(h => h.Id, h => h.UserId);
            foreach (var snapshot in snapshots)
            {
                if (!users.TryGetValue(owners[snapshot.Id], out var user)) continue;
                var habit = Habit.FromScheduleSnapshot(snapshot, user.Id);
                var longest = HabitMetricsCalculator.CalculateProjected(habit, byHabit[habit.Id].ToArray(),
                    todayByUser[user.Id], user.WeekStartDay, TimeZoneHelper.FindTimeZone(user.TimeZone)).LongestStreak;
                await db.Habits.Where(h => h.Id == habit.Id).ExecuteUpdateAsync(update => update
                    .SetProperty(h => h.LongestRecordedStreak, h => h.LongestRecordedStreak > longest ? h.LongestRecordedStreak : longest), ct);
            }
            cursor = batch[^1].Id;
        }
        if (!await db.AppConfigs.AnyAsync(config => config.Key == CompletionKey, ct))
        {
            db.AppConfigs.Add(AppConfig.Create(CompletionKey, "true", "Historical habit streak state is initialized"));
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task BackfillXpAsync(CancellationToken ct)
    {
        Guid? cursor = null;
        while (true)
        {
            var users = await PendingUsers(cursor).Select(user => user.Id).ToArrayAsync(ct);
            if (users.Length == 0) return;
            foreach (var userId in users)
                await db.Database.CreateExecutionStrategy().ExecuteAsync(() => BackfillUserXpAsync(userId, ct));
            cursor = users[^1];
        }
    }

    private async Task BackfillUserXpAsync(Guid userId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '1s'; SET LOCAL statement_timeout = '5s';", ct);
        await UserRowLock.AcquireAsync(db, userId, ct);
        if (!await db.Users.AnyAsync(user => user.Id == userId
                && EF.Property<bool?>(user, XpHistoryStore.InitializedProperty) != true, ct)) return;

        await db.XpDailyTotals.Where(row => row.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH daily AS (
                SELECT ("AwardedAtUtc" AT TIME ZONE 'UTC')::date AS day, SUM("Amount")::integer AS amount
                FROM "XpAwardLogs" WHERE "UserId" = {userId}
                GROUP BY ("AwardedAtUtc" AT TIME ZONE 'UTC')::date
            )
            INSERT INTO "XpDailyTotals" ("Id", "UserId", "Date", "DailyXp", "CumulativeXp")
            SELECT gen_random_uuid(), {userId}, day, amount, SUM(amount) OVER (ORDER BY day)::integer FROM daily;
            """, ct);
        await db.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(update => update
            .SetProperty(user => EF.Property<bool?>(user, XpHistoryStore.InitializedProperty), (bool?)true), ct);
        await transaction.CommitAsync(ct);
    }
}
