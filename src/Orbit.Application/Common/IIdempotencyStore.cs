namespace Orbit.Application.Common;

/// <summary>
/// Persists and retrieves idempotency-ledger records so a replayed client mutation (identified by an
/// Idempotency-Key) returns its original response instead of re-executing.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Returns the stored serialized response for a previously-processed command identity, or
    /// <c>null</c> if that combination has not been processed. An empty body is a committed reservation with
    /// no response: its call still runs, or its process stopped before it stored the response.
    /// </summary>
    Task<string?> FindResponseBodyAsync(Guid userId, string idempotencyKey, string requestType,
        int requestOrdinal, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a tracked, uncommitted reservation for the (user, key, request type, ordinal). The caller saves it,
    /// in the transaction of the wrapped handler or alone before a handler that calls a model provider.
    /// </summary>
    IIdempotencyReservation Reserve(Guid userId, string idempotencyKey, string requestType, int requestOrdinal);
}

/// <summary>
/// An idempotency reservation. A handler that runs in the reservation's transaction sets the response body on
/// the tracked row. A reservation that was committed alone stores its response or releases its key with one
/// statement of its own, so no pending change of the handler is saved with it.
/// </summary>
public interface IIdempotencyReservation
{
    void SetResponseBody(string responseBody);

    Task StoreResponseBodyAsync(string responseBody, CancellationToken cancellationToken);

    Task ReleaseAsync(CancellationToken cancellationToken);
}
