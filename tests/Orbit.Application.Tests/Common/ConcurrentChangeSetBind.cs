using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Common;

/// <summary>
/// An in-memory database where the first bind of a change set preview meets a concurrent write: a second context
/// revises the same row first, so the store refuses the bind on the revision concurrency token, as in production.
/// </summary>
internal sealed class ConcurrentChangeSetBind : SaveChangesInterceptor
{
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly string _name = "ConcurrentChangeSetBind_" + Guid.NewGuid();
    private bool _written;

    /// <summary>A context of the database that runs the concurrent write on the first bind of a change set.</summary>
    public OrbitDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(_name, _root).AddInterceptors(this).Options);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        WriteConcurrently(eventData.Context!);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        WriteConcurrently(eventData.Context!);
        return ValueTask.FromResult(result);
    }

    private void WriteConcurrently(DbContext context)
    {
        if (_written || context.ChangeTracker.Entries<PendingAgentOperationState>().FirstOrDefault(entry =>
                entry.State == EntityState.Modified && entry.Entity.OperationId == AccountChangeSet.OperationName) is not { } bind)
            return;
        _written = true;
        using var concurrent = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(_name, _root).Options);
        var stored = concurrent.PendingAgentOperations.Single(item => item.Id == bind.Entity.Id);
        if (!stored.Revise(stored.ArgumentsJson, stored.OperationFingerprint, "concurrent-preview", DateTime.UtcNow))
            throw new InvalidOperationException("The concurrent write did not revise the change set.");
        concurrent.SaveChanges();
    }
}
