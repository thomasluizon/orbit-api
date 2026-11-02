using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class XpHistoryStore(OrbitDbContext context) : IXpHistoryStore
{
    internal const string InitializedProperty = "XpHistoryInitialized";

    public async Task<int> ReadBaselineAsync(Guid userId, DateOnly from, CancellationToken cancellationToken = default)
    {
        var initialized = await context.Users.AsNoTracking()
            .AnyAsync(user => user.Id == userId && EF.Property<bool?>(user, InitializedProperty) == true, cancellationToken);
        if (!initialized)
        {
            var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            return await context.XpAwardLogs.Where(row => row.UserId == userId && row.AwardedAtUtc < fromUtc)
                .SumAsync(row => (int?)row.Amount, cancellationToken) ?? 0;
        }

        return await context.XpDailyTotals.AsNoTracking().Where(row => row.UserId == userId && row.Date < from)
            .OrderByDescending(row => row.Date).Select(row => (int?)row.CumulativeXp).FirstOrDefaultAsync(cancellationToken) ?? 0;
    }
}
