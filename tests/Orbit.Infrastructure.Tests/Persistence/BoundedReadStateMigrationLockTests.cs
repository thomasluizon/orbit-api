using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BoundedReadStateMigrationLockTests
{
    private const string Before = "20261102102120_PersistStripeCustomerErasureRecovery";

    [PostgresFact]
    public Task ContendedMigration_ReleasesQueuedLegacyWrites() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var userId = await InsertLegacyUser(db);
            await using var blocker = new NpgsqlConnection(db.Database.GetConnectionString());
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand("LOCK TABLE \"XpAwardLogs\" IN ROW EXCLUSIVE MODE", blocker, transaction))
                await command.ExecuteNonQueryAsync();
            await using var migrating = MigrationContext(db);
            var migration = migrating.GetService<IMigrator>().MigrateAsync();
            Exception? failure;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!await db.Database.SqlQueryRaw<bool>("""
                    SELECT EXISTS(SELECT 1 FROM pg_locks
                        WHERE relation = '"XpAwardLogs"'::regclass AND NOT granted) AS "Value"
                    """).SingleAsync(deadline.Token))
                    await Task.Delay(20, deadline.Token);
                db.Database.SetCommandTimeout(3);
                var write = () => InsertLegacyAward(db, userId);
                await write.Should().NotThrowAsync("a waiting migration must yield before an old writer times out");
            }
            finally
            {
                await transaction.DisposeAsync();
                failure = await Record.ExceptionAsync(() => migration);
            }

            (failure?.GetBaseException()).Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("55P03");
            await migrating.GetService<IMigrator>().MigrateAsync();
            (await db.XpDailyTotals.Select(total => total.CumulativeXp).SingleAsync()).Should().Be(7);
        }, Before);

    [PostgresFact]
    public Task SlowBackfill_RollsBackBeforeLegacyWritesTimeOutAndCanRetry() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var userId = await InsertLegacyUser(db);
            await InsertLegacyAward(db, userId);
            var slow = new SlowBackfillInterceptor();
            await using var migrating = MigrationContext(db, slow);
            var migration = migrating.GetService<IMigrator>().MigrateAsync();
            await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Exception? failure;
            try
            {
                db.Database.SetCommandTimeout(7);
                var write = () => InsertLegacyAward(db, userId);
                await write.Should().NotThrowAsync("the backfill deadline must release its locks before an old writer times out");
            }
            finally
            {
                failure = await Record.ExceptionAsync(() => migration);
            }

            (failure?.GetBaseException()).Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("57014");
            (await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('\"XpDailyTotals\"') IS NULL AS \"Value\"").SingleAsync())
                .Should().BeTrue("the failed migration must roll back its schema and partial totals atomically");
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET statement_timeout = '12s'; SET lock_timeout = '2s';");
            await db.GetService<IMigrator>().MigrateAsync();
            (await db.XpDailyTotals.Select(total => total.CumulativeXp).SingleAsync()).Should().Be(14);
            (await db.Database.SqlQueryRaw<string>("SELECT current_setting('statement_timeout') AS \"Value\"").SingleAsync())
                .Should().Be("12s");
            (await db.Database.SqlQueryRaw<string>("SELECT current_setting('lock_timeout') AS \"Value\"").SingleAsync())
                .Should().Be("2s");
            await db.Database.CloseConnectionAsync();
        }, Before);

    private static OrbitDbContext MigrationContext(OrbitDbContext db, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql(db.Database.GetConnectionString(), options => options.CommandTimeout(15))
            .AddInterceptors(interceptors).Options);

    private static Task InsertLegacyAward(OrbitDbContext db, Guid userId)
    {
        var atUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "XpAwardLogs" ("Id", "UserId", "Amount", "Source", "SourceId", "AwardedAtUtc", "CreatedAtUtc")
            VALUES ({Guid.NewGuid()}, {userId}, 7, 'HabitLog', NULL, {atUtc}, {atUtc})
            """);
    }

    private static async Task<Guid> InsertLegacyUser(OrbitDbContext db)
    {
        var userId = Guid.NewGuid();
        var atUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "Name", "Email", "CreatedAtUtc")
            VALUES ({userId}, 'Legacy', 'migration-lock@example.test', {atUtc})
            """);
        return userId;
    }

    private sealed class SlowBackfillInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DECLARE cursor_id", StringComparison.Ordinal))
            {
                await using var delay = (command.Connection ?? throw new InvalidOperationException("Migration needs a connection.")).CreateCommand();
                delay.Transaction = command.Transaction;
                delay.CommandText = """
                    CREATE FUNCTION slow_xp_backfill() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        PERFORM pg_sleep(8);
                        RETURN NEW;
                    END $$;
                    CREATE TRIGGER slow_xp_backfill BEFORE INSERT ON "XpDailyTotals"
                        FOR EACH ROW EXECUTE FUNCTION slow_xp_backfill();
                    """;
                await delay.ExecuteNonQueryAsync(cancellationToken);
                Started.TrySetResult();
            }
            return result;
        }
    }
}
