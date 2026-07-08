using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orbit.Domain.Entities;
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
        var unitOfWork = new UnitOfWork(context);

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
}
