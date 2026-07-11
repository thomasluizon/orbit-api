using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

public sealed class UnitOfWork(OrbitDbContext context) : IUnitOfWork, IAsyncDisposable
{
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

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Join an ambient transaction (e.g. IdempotencyBehavior's) rather than nest, which Npgsql forbids: https://github.com/thomasluizon/orbit-ui-mobile/issues/243
        if (!UseRelationalTransactionPath() || context.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken);
        }

        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var result = await operation(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                context.ChangeTracker.Clear();
                throw;
            }
        });
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

    private bool UseRelationalTransactionPath()
    {
        return context.Database.IsRelational()
            && context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) != true;
    }
}
