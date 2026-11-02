using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

public sealed record ApiKeyAuthenticationCandidate(ApiKey Key, bool OwnerIsDeactivated, bool HasProAccess, DateTime? EntitlementEndsAtUtc);

public interface IApiKeyAuthenticationStore
{
    Task<ApiKeyAuthenticationCandidate?> FindAsync(string prefix, CancellationToken cancellationToken);
}
