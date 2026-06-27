namespace Orbit.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
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
