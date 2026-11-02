using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class XpHistoryStore(OrbitDbContext context) : IXpHistoryStore
{
    public async Task<int> ReadBaselineAsync(Guid userId, DateOnly from, CancellationToken cancellationToken = default) =>
        await context.XpDailyTotals.AsNoTracking().Where(row => row.UserId == userId && row.Date < from)
            .OrderByDescending(row => row.Date).Select(row => (int?)row.CumulativeXp).FirstOrDefaultAsync(cancellationToken) ?? 0;
}
