namespace Orbit.Domain.Interfaces;

public interface IAiDailyBudget
{
    Task<Guid> ReserveAsync(Guid userId, long reservedTokens, CancellationToken cancellationToken);
    Task SettleAsync(Guid callId, long? tokens, CancellationToken cancellationToken);
}
