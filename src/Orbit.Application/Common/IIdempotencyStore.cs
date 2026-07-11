namespace Orbit.Application.Common;

/// <summary>
/// Persists and retrieves idempotency-ledger records so a replayed client mutation (identified by an
/// Idempotency-Key) returns its original response instead of re-executing. See
/// thomasluizon/orbit-ui-mobile#243.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Returns the stored serialized response for a previously-processed (user, key, request type), or
    /// <c>null</c> if that combination has not been processed. The request type scopes the key so reusing
    /// one key across two different commands never returns the wrong command's cached response.
    /// </summary>
    Task<string?> FindResponseBodyAsync(Guid userId, string idempotencyKey, string requestType, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a tracked, uncommitted reservation for the (user, key, request type) so it commits atomically
    /// with the wrapped handler's mutation. The response body is filled in via the returned reservation
    /// after the handler runs.
    /// </summary>
    IIdempotencyReservation Reserve(Guid userId, string idempotencyKey, string requestType);
}

/// <summary>
/// A tracked, not-yet-committed idempotency reservation whose response body is set after the wrapped
/// handler runs, so the reservation and the mutation persist in the same transaction.
/// </summary>
public interface IIdempotencyReservation
{
    void SetResponseBody(string responseBody);
}
