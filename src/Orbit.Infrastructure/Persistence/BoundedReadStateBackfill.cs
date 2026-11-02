using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Persistence;

public sealed class BoundedReadStateBackfill(OrbitDbContext db, IUserDateService dates)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        Guid? cursor = null;
        while (true)
        {
            var batch = await db.Habits.AsNoTracking().Where(h => h.LongestRecordedStreak == null
                    && (cursor == null || h.Id.CompareTo(cursor.Value) > 0))
                .OrderBy(h => h.Id).Take(100)
                .Select(h => new { h.Id, h.UserId }).ToArrayAsync(ct);
            if (batch.Length == 0) return;
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
    }
}
