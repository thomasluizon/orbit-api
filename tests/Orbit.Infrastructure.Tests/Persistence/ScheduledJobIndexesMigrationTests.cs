using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ScheduledJobIndexesMigrationTests
{
    [Fact]
    public void ConcurrentCreates_RepairInvalidIndexBeforeEveryCreate()
    {
        var migration = new ScheduledJobIndexes();
        foreach (var operations in new[] { migration.UpOperations, migration.DownOperations })
        {
            var sql = operations.Cast<SqlOperation>().ToList();
            foreach (var create in sql.Where(operation => operation.Sql.StartsWith("CREATE INDEX CONCURRENTLY", StringComparison.Ordinal)))
            {
                var previous = sql.TakeWhile(operation => operation != create).LastOrDefault();
                previous.Should().NotBeNull();
                previous!.SuppressTransaction.Should().BeTrue();
                previous.Sql.Should().Contain("pg_index").And.Contain("indisvalid").And.Contain("DROP INDEX IF EXISTS");
                var indexName = create.Sql.Split('"')[1];
                previous.Sql.Should().Contain(indexName);
                create.SuppressTransaction.Should().BeTrue();
            }
        }
    }

    [PostgresFact]
    public async Task Up_InvalidIndexFromFailedConcurrentBuild_IsReplacedAndRerunIsIdempotent()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            var user = User.Create("Indexes", "indexes@example.com").Value;
            var first = Notification.Create(user.Id, "First", "Body", "/");
            var second = Notification.Create(user.Id, "Second", "Body", "/");
            typeof(Notification).GetProperty(nameof(Notification.CreatedAtUtc))!.SetValue(second, first.CreatedAtUtc);
            db.AddRange(user, first, second);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""DROP INDEX IF EXISTS "IX_Notifications_CreatedAtUtc";""");
            var interruptedBuild = () => db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX CONCURRENTLY "IX_Notifications_CreatedAtUtc" ON "Notifications" ("CreatedAtUtc");""");
            (await interruptedBuild.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
            var valid = () => db.Database.SqlQuery<bool>($"""
                SELECT indisvalid AS "Value" FROM pg_catalog.pg_index
                WHERE indexrelid = '"IX_Notifications_CreatedAtUtc"'::regclass
                """).SingleAsync();
            (await valid()).Should().BeFalse();

            foreach (var operation in new ScheduledJobIndexes().UpOperations.Cast<SqlOperation>())
                await db.Database.ExecuteSqlRawAsync(operation.Sql);

            (await valid()).Should().BeTrue();
            foreach (var operation in new ScheduledJobIndexes().UpOperations.Cast<SqlOperation>())
                await db.Database.ExecuteSqlRawAsync(operation.Sql);
            (await valid()).Should().BeTrue();
        });
    }
}
