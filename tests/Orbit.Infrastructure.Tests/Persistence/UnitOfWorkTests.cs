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
}
