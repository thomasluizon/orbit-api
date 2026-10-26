namespace Orbit.Domain.Interfaces;

public interface IDistributedQuotaService
{
    Task<bool> TryAcquireAsync(string policy, string partition, int limit, int amount,
        TimeSpan window, CancellationToken cancellationToken);
}
