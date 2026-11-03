namespace Orbit.Domain.Interfaces;

public interface IXpHistoryStore
{
    Task<int> ReadBaselineAsync(Guid userId, DateOnly from, CancellationToken cancellationToken = default);
}
