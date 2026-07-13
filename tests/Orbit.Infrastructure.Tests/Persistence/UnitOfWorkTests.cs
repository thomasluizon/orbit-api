using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class UnitOfWorkTests
{
    [Fact]
    public async Task ExecuteInTransactionAsync_WhenOperationThrows_ClearsTrackedEntitiesAndSurfacesOriginalException()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new OrbitDbContext(options);
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());

        var conflict = new DbUpdateException(
            "value too long for type character varying(256)",
            new PostgresException(
                "value too long for type character varying(256)",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.StringDataRightTruncation));

        var execute = () => unitOfWork.ExecuteInTransactionAsync(_ =>
        {
            context.DistributedRateLimitBuckets.Add(DistributedRateLimitBucket.Create(
                "auth",
                "user:leak",
                DateTime.UtcNow,
                DateTime.UtcNow.AddMinutes(1)));
            throw conflict;
        });

        (await execute.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(conflict);
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_WhenAmbientTransactionActive_RunsInlineWithoutNesting()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new OrbitDbContext(options);
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());

        await using var ambientTransaction = await context.Database.BeginTransactionAsync();

        var operationRan = false;
        var act = () => unitOfWork.ExecuteInTransactionAsync(_ =>
        {
            operationRan = true;
            return Task.CompletedTask;
        });

        await act.Should().NotThrowAsync();
        operationRan.Should().BeTrue();
        context.Database.CurrentTransaction.Should().BeSameAs(ambientTransaction);
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_WhenOperationExceedsTimeout_ThrowsTimeoutExceptionAndRollsBack()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new OrbitDbContext(options);
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings { TransactionTimeoutSeconds = 1 });

        var act = () => unitOfWork.ExecuteInTransactionAsync(async token =>
            await Task.Delay(TimeSpan.FromSeconds(30), token));

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*1s timeout*");
        context.Database.CurrentTransaction.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_WhenCallerCancels_SurfacesCancellationNotTimeout()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new OrbitDbContext(options);
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());

        using var cancellation = new CancellationTokenSource();

        var act = () => unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), token);
        }, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        context.Database.CurrentTransaction.Should().BeNull();
    }

    [Fact]
    public async Task DiscardChanges_ResetsTrackerSoSubsequentSaveChangesPersistsNothing()
    {
        var databaseName = NewInMemoryDatabaseName();
        Guid modifiedId, deletedId;
        await using (var seed = CreateInMemoryContext(databaseName))
        {
            var toModify = Bucket("modify");
            var toDelete = Bucket("delete");
            seed.DistributedRateLimitBuckets.AddRange(toModify, toDelete);
            await seed.SaveChangesAsync();
            modifiedId = toModify.Id;
            deletedId = toDelete.Id;
        }

        await using var context = CreateInMemoryContext(databaseName);
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());

        var modified = context.DistributedRateLimitBuckets.Single(bucket => bucket.Id == modifiedId);
        modified.Increment();
        var added = Bucket("added");
        context.DistributedRateLimitBuckets.Add(added);
        var deleted = context.DistributedRateLimitBuckets.Single(bucket => bucket.Id == deletedId);
        context.DistributedRateLimitBuckets.Remove(deleted);

        context.Entry(modified).State.Should().Be(EntityState.Modified);
        context.Entry(added).State.Should().Be(EntityState.Added);
        context.Entry(deleted).State.Should().Be(EntityState.Deleted);

        unitOfWork.DiscardChanges();

        context.Entry(modified).State.Should().Be(EntityState.Unchanged);
        context.Entry(added).State.Should().Be(EntityState.Detached);
        context.Entry(deleted).State.Should().Be(EntityState.Unchanged);
        (await unitOfWork.SaveChangesAsync()).Should().Be(0);

        await using var verify = CreateInMemoryContext(databaseName);
        verify.DistributedRateLimitBuckets.Single(bucket => bucket.Id == modifiedId).Count.Should().Be(1);
        verify.DistributedRateLimitBuckets.Any(bucket => bucket.Id == added.Id).Should().BeFalse();
        verify.DistributedRateLimitBuckets.Any(bucket => bucket.Id == deletedId).Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAdvisoryLockAsync_OnNonPostgresProvider_IsSynchronousNoOp()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new OrbitDbContext(options);
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());

        // A synchronously-completed task proves the no-op branch fired instead of issuing SQL (SQLite has no pg_advisory_xact_lock); the real Postgres lock path needs a live server, out of reach here: https://github.com/thomasluizon/orbit-ui-mobile/issues/243
        var lockTask = unitOfWork.AcquireAdvisoryLockAsync("sync:user:42");

        lockTask.IsCompletedSuccessfully.Should().BeTrue();
        await lockTask;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AcquireAdvisoryLockAsync_WithMissingKey_Throws(string? key)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new OrbitDbContext(options);
        var unitOfWork = new UnitOfWork(context, new DatabaseConnectionSettings());

        var act = () => unitOfWork.AcquireAdvisoryLockAsync(key!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static string NewInMemoryDatabaseName() => $"UnitOfWorkTests_{Guid.NewGuid()}";

    private static OrbitDbContext CreateInMemoryContext(string databaseName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(databaseName).Options);

    private static DistributedRateLimitBucket Bucket(string partitionKey) =>
        DistributedRateLimitBucket.Create(
            "auth", partitionKey, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1));
}
