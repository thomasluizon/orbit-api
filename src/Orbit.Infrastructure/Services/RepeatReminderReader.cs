using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class RepeatReminderReader(OrbitDbContext db, IUserDateService dates, TimeProvider time)
    : IRepeatReminderReader
{
    public async Task<IReadOnlyDictionary<Guid, DateTime?>> GetNextAsync(
        Guid userId, IReadOnlyCollection<Guid> habitIds, CancellationToken cancellationToken)
    {
        if (habitIds.Count == 0)
            return new Dictionary<Guid, DateTime?>();
        var habits = await db.Habits.AsNoTracking().Where(h => h.UserId == userId && habitIds.Contains(h.Id)
            && h.ReminderEnabled && h.RepeatIntervalMinutes != null && !h.IsCompleted).ToListAsync(cancellationToken);
        if (habits.Count == 0)
            return new Dictionary<Guid, DateTime?>();
        var nowAtUtc = time.GetUtcNow().UtcDateTime;
        var zone = await dates.GetUserTimeZoneAsync(userId, cancellationToken);
        var today = await dates.GetUserTodayAsync(userId, cancellationToken);
        var weekStartDay = await dates.GetUserWeekStartDayAsync(userId, cancellationToken);
        var anchors = await RepeatReminderDoseLoader.LoadLatestAsync(new GenericRepository<HabitLog>(db),
            habits.ToDictionary(h => h.Id, _ => zone), cancellationToken);
        var recurringIds = habits.Where(h => h.Schedule == HabitSchedule.Recurring).Select(h => h.Id).ToList();
        var loggedDates = (await db.HabitLogs.AsNoTracking()
            .Where(l => recurringIds.Contains(l.HabitId) && l.Value > 0 && l.Date >= today)
            .Select(l => new { l.HabitId, l.Date }).Distinct().ToListAsync(cancellationToken))
            .ToLookup(l => l.HabitId, l => l.Date);
        var sent = (await RepeatReminderSnapshotQueries.LoadSentAsync(db, habitIds, nowAtUtc, cancellationToken))
            .ToLookup(r => r.HabitId);
        return habits.ToDictionary(h => h.Id, h => RepeatReminderPlanner.Next(h, nowAtUtc, zone, weekStartDay,
            anchors.TryGetValue(h.Id, out var anchorAtUtc) ? anchorAtUtc : null,
            loggedDates[h.Id].ToHashSet(), sent[h.Id].ToList())?.DispatchAtUtc);
    }
}
