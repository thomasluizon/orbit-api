using FluentAssertions;
using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ScheduledJobIndexesMigrationTests
{
    [Fact]
    public void ConcurrentCreates_RepairIndexesOutsideTransactionsWithoutBlockingReaders()
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
                previous.Sql.Should().StartWith("DROP INDEX CONCURRENTLY IF EXISTS");
                var indexName = create.Sql.Split('"')[1];
                previous.Sql.Should().Contain(indexName);
                create.SuppressTransaction.Should().BeTrue();
            }
        }
    }

    [Theory]
    [InlineData(typeof(ScheduledJobIndexes))]
    [InlineData(typeof(PersistMarketingBroadcastDeliveries))]
    public void ScheduledJobMigrations_UpAndDownMeetLockRulesWithoutExceptions(Type migrationType)
    {
        var migration = (Migration)Activator.CreateInstance(migrationType)!;
        var id = migrationType.GetCustomAttribute<MigrationAttribute>()!.Id;
        var policy = new MigrationLockPolicy("20261101210800_PersistServerConversations", "applied in production before this gate", []);
        foreach (var operations in new[] { migration.UpOperations, migration.DownOperations })
            MigrationLockGate.Check(id, operations, policy).Should().BeEmpty();
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

            await db.Database.OpenConnectionAsync();
            foreach (var operation in new ScheduledJobIndexes().UpOperations.Cast<SqlOperation>())
                await db.Database.ExecuteSqlRawAsync(operation.Sql);

            (await valid()).Should().BeTrue();
            foreach (var operation in new ScheduledJobIndexes().UpOperations.Cast<SqlOperation>())
                await db.Database.ExecuteSqlRawAsync(operation.Sql);
            (await valid()).Should().BeTrue();
        });
    }

    [PostgresFact]
    public async Task Up_ContendedInvalidIndexRepair_BoundsWaitAndLeavesApplicationReadsAvailable()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            var user = User.Create("Contention", "contention@example.com").Value;
            var first = Notification.Create(user.Id, "First", "Body", "/");
            var second = Notification.Create(user.Id, "Second", "Body", "/");
            typeof(Notification).GetProperty(nameof(Notification.CreatedAtUtc))!.SetValue(second, first.CreatedAtUtc);
            db.AddRange(user, first, second);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""DROP INDEX IF EXISTS "IX_Notifications_CreatedAtUtc";""");
            var failedBuild = () => db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX CONCURRENTLY "IX_Notifications_CreatedAtUtc" ON "Notifications" ("CreatedAtUtc");""");
            await failedBuild.Should().ThrowAsync<PostgresException>();

            await using var reader = new NpgsqlConnection(db.Database.GetConnectionString());
            await reader.OpenAsync();
            await using var heldRead = await reader.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("""SELECT COUNT(*) FROM "Notifications";""", reader, heldRead))
                await hold.ExecuteScalarAsync();
            await using var probe = new NpgsqlConnection(db.Database.GetConnectionString());
            await probe.OpenAsync();
            await db.Database.OpenConnectionAsync();
            var repairPid = await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            var elapsed = Stopwatch.StartNew();
            async Task Repair()
            {
                foreach (var operation in new ScheduledJobIndexes().UpOperations.Cast<SqlOperation>())
                    await db.Database.ExecuteSqlRawAsync(operation.Sql, deadline.Token);
            }
            var repair = Repair();
            var pending = false;
            for (var attempt = 0; attempt < 60 && !pending && !repair.IsCompleted; attempt++)
            {
                await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid = @pid AND NOT granted);", probe);
                waiting.Parameters.AddWithValue("pid", repairPid);
                pending = (bool)(await waiting.ExecuteScalarAsync())!;
                if (!pending) await Task.Delay(50);
            }
            await using (var timeout = new NpgsqlCommand("SET lock_timeout = '500ms';", probe))
                await timeout.ExecuteNonQueryAsync();
            var readFailure = await Record.ExceptionAsync(async () =>
            {
                await using var read = new NpgsqlCommand("""SELECT COUNT(*) FROM "Notifications";""", probe);
                (await read.ExecuteScalarAsync()).Should().Be(2L);
            });
            var repairFailure = await Record.ExceptionAsync(() => repair);

            pending.Should().BeTrue("the existing reader must hold the index repair open");
            readFailure.Should().BeNull("an index repair must not block unrelated application reads");
            repairFailure.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("55P03");
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "lock waits must be bounded by the migration's five-second timeout");
        });
    }
}
