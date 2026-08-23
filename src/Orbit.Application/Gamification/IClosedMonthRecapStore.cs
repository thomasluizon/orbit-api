using Orbit.Domain.Entities;

namespace Orbit.Application.Gamification;

/// <summary>
/// Persists the first response computed for an account and resolved closed month so later requests
/// return the same result even when mutable habit cadence data has changed.
/// </summary>
public interface IClosedMonthRecapStore
{
    Task<string?> FindResponseJsonAsync(
        Guid userId,
        DateOnly dateFrom,
        DateOnly dateTo,
        CancellationToken cancellationToken);

    Task AddAsync(ClosedMonthRecap recap, CancellationToken cancellationToken);
}
