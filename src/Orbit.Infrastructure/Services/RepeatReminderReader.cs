using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
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
        var eligibility = await RepeatReminderEligibility.LoadAsync(new HabitScheduleLogReader(db),
            habits, today, today.AddDays(2), cancellationToken);
        var sent = (await RepeatReminderSnapshotQueries.LoadSentAsync(db, habitIds, nowAtUtc, cancellationToken))
            .ToLookup(r => r.HabitId);
        return habits.ToDictionary(h => h.Id, h => RepeatReminderPlanner.Next(h, nowAtUtc, zone, weekStartDay,
            anchors.TryGetValue(h.Id, out var anchorAtUtc) ? anchorAtUtc : null,
            eligibility, sent[h.Id].ToList())?.DispatchAtUtc);
    }
}
