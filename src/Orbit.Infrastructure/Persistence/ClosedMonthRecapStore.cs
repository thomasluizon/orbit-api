using Microsoft.EntityFrameworkCore;
using Orbit.Application.Gamification;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

public sealed class ClosedMonthRecapStore(OrbitDbContext context) : IClosedMonthRecapStore
{
    public Task<string?> FindResponseJsonAsync(
        Guid userId,
        DateOnly dateFrom,
        DateOnly dateTo,
        CancellationToken cancellationToken)
    {
        return context.ClosedMonthRecaps
            .AsNoTracking()
            .Where(recap => recap.UserId == userId
                && recap.DateFrom == dateFrom
                && recap.DateTo == dateTo)
            .Select(recap => recap.ResponseJson)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task AddAsync(ClosedMonthRecap recap, CancellationToken cancellationToken)
    {
        return context.ClosedMonthRecaps.AddAsync(recap, cancellationToken).AsTask();
    }
}
