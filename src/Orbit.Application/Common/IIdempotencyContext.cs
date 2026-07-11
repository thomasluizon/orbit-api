namespace Orbit.Application.Common;

/// <summary>
/// Surfaces the current request's client idempotency key and authenticated user, if both are present,
/// so the idempotency pipeline behavior can dedupe replayed mutations without depending on ASP.NET Core.
/// </summary>
public interface IIdempotencyContext
{
    bool TryGetRequestKey(out Guid userId, out string idempotencyKey);
}
