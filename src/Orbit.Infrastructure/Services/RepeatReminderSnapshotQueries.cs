using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

internal static class RepeatReminderSnapshotQueries
{
    internal static Task<List<SentReminder>> LoadSentAsync(OrbitDbContext db, IReadOnlyCollection<Guid> habitIds,
        DateTime nowAtUtc, CancellationToken cancellationToken) =>
        db.SentReminders.AsNoTracking()
            .Where(r => habitIds.Contains(r.HabitId) && r.ReminderAtUtc >= nowAtUtc.AddDays(-3))
            .ToListAsync(cancellationToken);
}
