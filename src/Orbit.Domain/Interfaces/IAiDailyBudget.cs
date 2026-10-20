namespace Orbit.Domain.Interfaces;

public interface IAiDailyBudget
{
    Task<Guid> ReserveAsync(Guid userId, long reservedMicroDollars, CancellationToken cancellationToken);
    Task SettleAsync(Guid callId, long microDollars, CancellationToken cancellationToken);
}
