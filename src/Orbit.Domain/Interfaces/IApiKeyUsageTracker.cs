namespace Orbit.Domain.Interfaces;

public interface IApiKeyUsageTracker
{
    Task MarkUsedAsync(Guid keyId, CancellationToken cancellationToken = default);
}
