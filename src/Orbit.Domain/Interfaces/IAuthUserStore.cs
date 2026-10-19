using Orbit.Domain.Entities;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// Persists an authenticated candidate with a write on both insert and conflict, then reads the
/// tracked account. Call inside the sign-in transaction. The next save must also write the account
/// even when its values are unchanged.
/// </summary>
public interface IAuthUserStore
{
    Task<User> UpsertAsync(User candidate, CancellationToken cancellationToken);
}
