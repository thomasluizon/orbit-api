namespace Orbit.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops every pending change: detaches added entities and reverts modified/deleted ones to
    /// Unchanged. Used after a concurrency conflict to clear stale side-rows (e.g. an audit log)
    /// before reloading and replaying, so a retry doesn't double-insert them.
    /// </summary>
    void DiscardChanges();
}
