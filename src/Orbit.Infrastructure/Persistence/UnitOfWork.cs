using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Events;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Persistence;

public sealed class UnitOfWork(
    OrbitDbContext context,
    DatabaseConnectionSettings databaseSettings,
    IAccountEventCollector? eventCollector = null,
    HabitScheduleSnapshotStore? scheduleSnapshots = null)
    : IUnitOfWork, IAsyncDisposable
{
    private readonly List<Action> _afterCommit = [];
    public bool HasActiveTransaction => context.Database.CurrentTransaction is not null;

    public void AfterCommit(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (HasActiveTransaction) _afterCommit.Add(callback);
        else callback();
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return context.SaveChangesAsync(cancellationToken);
    }

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await ExecuteInTransactionAsync<object?>(async ct =>
        {
            await operation(ct);
            return null;
        }, cancellationToken);
    }

    public Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) => ExecuteTransactionAsync(operation, cancellationToken, true);

    public Task<T> ExecuteReplayableInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) => ExecuteTransactionAsync(operation, cancellationToken, false);

    private async Task<T> ExecuteTransactionAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken, bool verifyCommit)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Join an ambient transaction (e.g. IdempotencyBehavior's) rather than nest, which Npgsql forbids: https://github.com/thomasluizon/orbit-ui-mobile/issues/243
        if (!UseRelationalTransactionPath() || context.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken);
        }

        using var deadline = new TransactionDeadline(context, databaseSettings, cancellationToken);

        try
        {
            var result = await VerifiedTransaction.ExecuteAsync(context, operation, () =>
            {
                context.ChangeTracker.Clear();
                scheduleSnapshots?.Invalidate();
                eventCollector?.Clear();
                _afterCommit.Clear();
            }, deadline.OperationToken, deadline.CompletionToken, verifyCommit);
            DispatchAfterCommit();
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            _afterCommit.Clear();
            throw new TimeoutException(
                $"Transaction exceeded the {databaseSettings.TransactionTimeoutSeconds}s timeout and was rolled back.");
        }
        catch
        {
            _afterCommit.Clear();
            throw;
        }
    }

    public async Task ExecuteOnceInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!UseRelationalTransactionPath() || context.Database.CurrentTransaction is not null)
        {
            await operation(cancellationToken);
            return;
        }

        using var deadline = new TransactionDeadline(context, databaseSettings, cancellationToken);
        var token = deadline.OperationToken;
        try
        {
            var strategy = context.Database.CreateExecutionStrategy();
            await using var transaction = await strategy.ExecuteAsync(() => context.Database.BeginTransactionAsync(token));
            await new SingleAttemptExecutionStrategy(context).ExecuteAsync(async () =>
            {
                try
                {
                    await operation(token);
                    await transaction.CommitAsync(deadline.CompletionToken);
                }
                catch
                {
                    context.ChangeTracker.Clear();
                    scheduleSnapshots?.Invalidate();
                    eventCollector?.Clear();
                    _afterCommit.Clear();
                    throw;
                }
            });
            DispatchAfterCommit();
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            throw new TimeoutException(
                $"Transaction exceeded the {databaseSettings.TransactionTimeoutSeconds}s timeout and was rolled back.");
        }
    }

    public async Task<T> ExecuteInSavepointAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var transaction = context.Database.CurrentTransaction;
        if (transaction is null)
            return await ExecuteInTransactionAsync(operation, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var completionCts = CancellationTokenSource.CreateLinkedTokenSource(context.TransactionCompletionToken ?? CancellationToken.None);
        completionCts.CancelAfter(TimeSpan.FromSeconds(databaseSettings.CommandTimeoutSeconds));
        var name = $"orbit_{Guid.NewGuid():N}";
        var callbackCount = _afterCommit.Count;
        await transaction.CreateSavepointAsync(name, completionCts.Token);
        try
        {
            var result = await operation(cancellationToken);
            await transaction.ReleaseSavepointAsync(name, completionCts.Token);
            return result;
        }
        catch
        {
            try { await transaction.RollbackToSavepointAsync(name, CancellationToken.None); }
            finally
            {
                context.ChangeTracker.Clear();
                scheduleSnapshots?.Invalidate();
                eventCollector?.Clear();
                _afterCommit.RemoveRange(callbackCount, _afterCommit.Count - callbackCount);
            }
            throw;
        }
    }

    public Task AcquireAdvisoryLockAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return Task.CompletedTask;

        return context.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtext({0}))",
            [key],
            cancellationToken);
    }

    public Task AcquireUserRowLockAsync(Guid userId, CancellationToken cancellationToken = default) =>
        UserRowLock.AcquireAsync(context, userId, cancellationToken);

    public void DiscardChanges()
    {
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            entry.State = entry.State switch
            {
                EntityState.Added => EntityState.Detached,
                EntityState.Modified or EntityState.Deleted => EntityState.Unchanged,
                _ => entry.State
            };
        }
    }

    public void ResetTracking()
    {
        context.ChangeTracker.Clear();
        scheduleSnapshots?.Invalidate();
    }

    private void DispatchAfterCommit()
    {
        var callbacks = _afterCommit.ToArray();
        _afterCommit.Clear();
        foreach (var callback in callbacks) callback();
    }

    public void Dispose()
    {
        context.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private sealed class SingleAttemptExecutionStrategy(OrbitDbContext context)
        : ExecutionStrategy(context, 0, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }

    private bool UseRelationalTransactionPath()
    {
        return context.Database.IsRelational()
            && context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) != true;
    }
}
