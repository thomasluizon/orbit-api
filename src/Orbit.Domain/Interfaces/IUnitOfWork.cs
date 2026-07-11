namespace Orbit.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="operation"/> in a transaction and returns its value, so callers can flow a
    /// result straight out of the transaction instead of smuggling it through a captured mutable local.
    /// Same transaction semantics as the void overload: joins an ambient transaction when one is active,
    /// otherwise wraps the operation in an execution-strategy-managed transaction that commits on success
    /// and clears the change tracker before rethrowing on failure.
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a transaction-scoped PostgreSQL advisory lock derived from <paramref name="key"/>, serializing
    /// callers that pass the same key until the surrounding transaction commits or rolls back. Must be
    /// called inside an <see cref="ExecuteInTransactionAsync"/> operation — the lock auto-releases at
    /// transaction end. No-ops on non-PostgreSQL providers (the in-memory and SQLite test databases),
    /// which have no advisory locks.
    /// </summary>
    Task AcquireAdvisoryLockAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops every pending change: detaches added entities and reverts modified/deleted ones to
    /// Unchanged. Used after a concurrency conflict to clear stale side-rows (e.g. an audit log)
    /// before reloading and replaying, so a retry doesn't double-insert them.
    /// </summary>
    void DiscardChanges();

    /// <summary>
    /// Detaches every tracked entity so the next query reloads from the database. Used by the
    /// concurrency-retry pipeline between attempts so a re-run sees current state rather than the
    /// stale tracked instances from the conflicted attempt.
    /// </summary>
    void ResetTracking();
}
