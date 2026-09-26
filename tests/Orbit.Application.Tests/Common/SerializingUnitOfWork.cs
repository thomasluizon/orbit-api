using System.Collections.Concurrent;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

internal sealed class SerializingUnitOfWork : IUnitOfWork
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly AsyncLocal<TransactionState?> _transaction = new();
    private readonly object _orderGate = new();
    private readonly List<string> _order = [];
    private int _lockRequestCount;

    public TaskCompletionSource<bool> SecondLockRequested { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Order
    {
        get { lock (_orderGate) { return [.. _order]; } }
    }

    public void Record(string entry)
    {
        lock (_orderGate) { _order.Add(entry); }
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Record("save");
        return Task.FromResult(0);
    }

    public Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteInTransactionAsync(async token =>
        {
            await operation(token);
            return true;
        }, cancellationToken);

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var previous = _transaction.Value;
        var current = new TransactionState();
        _transaction.Value = current;
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            current.HeldLock?.Release();
            _transaction.Value = previous;
        }
    }

    public async Task AcquireAdvisoryLockAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var transaction = _transaction.Value
            ?? throw new InvalidOperationException("A transaction is required for an advisory lock.");
        if (Interlocked.Increment(ref _lockRequestCount) == 2)
            SecondLockRequested.TrySetResult(true);

        var ceilingLock = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await ceilingLock.WaitAsync(cancellationToken);
        transaction.HeldLock = ceilingLock;
        Record($"lock:{key}");
    }

    public void DiscardChanges()
    {
    }

    public void ResetTracking()
    {
    }

    public void Dispose()
    {
    }

    private sealed class TransactionState
    {
        public SemaphoreSlim? HeldLock { get; set; }
    }
}
