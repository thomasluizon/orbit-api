using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class TransactionCancellationTests
{
    [Fact]
    public async Task CommitReceipt_DisconnectedCallerAndLostAcknowledgement_DoesNotRepeatWrite()
    {
        using var caller = new CancellationTokenSource();
        var acknowledgement = new LostAcknowledgement();
        using var factory = new SqliteOrbitDbContextFactory(
            dependencies => new NpgsqlRetryingExecutionStrategy(dependencies, 1, TimeSpan.Zero, null), acknowledgement);
        var db = factory.Context;
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var writes = 0;
        acknowledgement.Enabled = true;

        var result = await work.ExecuteInTransactionAsync(async token =>
        {
            writes++;
            db.DistributedRateLimitBuckets.Add(DistributedRateLimitBucket.Create(
                "auth", "commit-receipt", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1)));
            await work.SaveChangesAsync(token);
            caller.Cancel();
            return "completed";
        }, caller.Token);

        result.Should().Be("completed");
        writes.Should().Be(1);
        acknowledgement.Failures.Should().Be(1);
        db.Database.CurrentTransaction.Should().BeNull();
        (await db.DistributedRateLimitBuckets.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("replayable")]
    [InlineData("once")]
    public async Task CompletedWrite_CommitsAfterCallerDisconnectsWithoutTransactionError(string mode)
    {
        var errors = new TransactionErrors();
        using var factory = new SqliteOrbitDbContextFactory(errors);
        var db = factory.Context;
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        using var caller = new CancellationTokenSource();
        var bucket = DistributedRateLimitBucket.Create("auth", "completed", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1));

        await Execute(work, mode, async token =>
        {
            db.DistributedRateLimitBuckets.Add(bucket);
            await work.SaveChangesAsync(token);
            caller.Cancel();
        }, caller.Token);

        db.Database.CurrentTransaction.Should().BeNull();
        errors.Failures.Should().BeEmpty();
        db.ChangeTracker.Clear();
        (await db.DistributedRateLimitBuckets.AnyAsync(row => row.Id == bucket.Id)).Should().BeTrue();
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("replayable")]
    [InlineData("once")]
    public async Task InterruptedWrite_RollsBackAndReleasesTransactionWithoutTransactionError(string mode)
    {
        var errors = new TransactionErrors();
        using var factory = new SqliteOrbitDbContextFactory(errors);
        var db = factory.Context;
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        using var caller = new CancellationTokenSource();
        var bucket = DistributedRateLimitBucket.Create("auth", "interrupted", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1));
        var execute = () => Execute(work, mode, async token =>
        {
            db.DistributedRateLimitBuckets.Add(bucket);
            await work.SaveChangesAsync(token);
            caller.Cancel();
            bucket.Increment();
            await work.SaveChangesAsync(token);
        }, caller.Token);

        await execute.Should().ThrowAsync<OperationCanceledException>();

        db.Database.CurrentTransaction.Should().BeNull();
        errors.Failures.Should().BeEmpty();
        db.ChangeTracker.Entries().Should().BeEmpty();
        (await db.DistributedRateLimitBuckets.AnyAsync(row => row.Id == bucket.Id)).Should().BeFalse();
        await work.ExecuteInTransactionAsync(_ => Task.CompletedTask);
    }

    [Fact]
    public async Task CompletedSavepoint_ReleasesAfterCallerDisconnectsWithoutTransactionError()
    {
        var errors = new TransactionErrors();
        using var factory = new SqliteOrbitDbContextFactory(errors);
        var work = new UnitOfWork(factory.Context, new DatabaseConnectionSettings());
        using var caller = new CancellationTokenSource();

        await work.ExecuteInTransactionAsync(token => work.ExecuteInSavepointAsync(_ =>
        {
            caller.Cancel();
            return Task.FromResult(true);
        }, token), caller.Token);

        errors.Failures.Should().BeEmpty();
        factory.Context.Database.CurrentTransaction.Should().BeNull();
    }

    [Fact]
    public async Task DatabaseSave_CallerDisconnectsDuringBatch_CompletesWithoutTransactionError()
    {
        using var caller = new CancellationTokenSource();
        var errors = new TransactionErrors();
        using var factory = new SqliteOrbitDbContextFactory(errors, new DisconnectDuringInsert(caller));
        var db = factory.Context;
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());

        await work.ExecuteInTransactionAsync(async token =>
        {
            db.DistributedRateLimitBuckets.Add(DistributedRateLimitBucket.Create(
                "auth", "disconnect-during-save", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1)));
            await work.SaveChangesAsync(token);
        }, caller.Token);

        caller.IsCancellationRequested.Should().BeTrue();
        errors.Failures.Should().BeEmpty();
        db.Database.CurrentTransaction.Should().BeNull();
        (await db.DistributedRateLimitBuckets.CountAsync()).Should().Be(1);
    }

    private static Task Execute(UnitOfWork work, string mode, Func<CancellationToken, Task> operation, CancellationToken token) =>
        mode switch
        {
            "once" => work.ExecuteOnceInTransactionAsync(operation, token),
            "replayable" => work.ExecuteReplayableInTransactionAsync(async ct => { await operation(ct); return true; }, token),
            _ => work.ExecuteInTransactionAsync(operation, token)
        };

    private sealed class TransactionErrors : DbTransactionInterceptor
    {
        public List<Exception> Failures { get; } = [];

        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Failures.Add(eventData.Exception);
            return Task.CompletedTask;
        }
    }

    private sealed class DisconnectDuringInsert(CancellationTokenSource caller) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("INSERT INTO \"DistributedRateLimitBuckets\"", StringComparison.Ordinal))
                caller.Cancel();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public int Failures { get; private set; }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && Failures == 0)
            {
                Failures++;
                throw new NpgsqlException("Commit acknowledgement lost after committing", new TimeoutException());
            }
            return Task.CompletedTask;
        }
    }
}
