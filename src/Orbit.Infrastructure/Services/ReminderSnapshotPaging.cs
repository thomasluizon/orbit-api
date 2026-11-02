using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Services;

internal static class ReminderSnapshotPaging
{
    internal static async Task<List<Habit>> LoadReminderPageAsync(this IQueryable<Habit> query, CancellationToken ct)
    {
        var rows = new List<Habit>();
        Guid? after = null;
        while (true)
        {
            var page = await query.Where(h => after == null || h.Id.CompareTo(after.Value) > 0)
                .OrderBy(h => h.Id).Take(256).ToListAsync(ct);
            rows.AddRange(page);
            if (page.Count < 256) return rows;
            after = page[^1].Id;
        }
    }
}
