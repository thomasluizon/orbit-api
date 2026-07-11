namespace Orbit.Application.Common;

/// <summary>
/// Persists and retrieves idempotency-ledger records so a replayed client mutation (identified by an
/// Idempotency-Key) returns its original response instead of re-executing. See
/// thomasluizon/orbit-ui-mobile#243.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Returns the stored serialized response for a previously-processed key, or <c>null</c> if the key
    /// has not been processed for this user.
    /// </summary>
    Task<string?> FindResponseBodyAsync(Guid userId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a tracked, uncommitted reservation for the key so it commits atomically with the wrapped
    /// handler's mutation. The response body is filled in via the returned reservation after the handler runs.
    /// </summary>
    IIdempotencyReservation Reserve(Guid userId, string idempotencyKey);
}

/// <summary>
/// A tracked, not-yet-committed idempotency reservation whose response body is set after the wrapped
/// handler runs, so the reservation and the mutation persist in the same transaction.
/// </summary>
public interface IIdempotencyReservation
{
    void SetResponseBody(string responseBody);
}
