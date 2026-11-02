using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NSubstitute;
using Orbit.Domain.Interfaces;
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
            await new BoundedReadStateBackfill(migrating, Substitute.For<IUserDateService>()).RunAsync();
            (await db.XpDailyTotals.Select(total => total.CumulativeXp).SingleAsync()).Should().Be(7);
        }, Before);

    [PostgresFact]
    public Task SlowUserBackfill_CommitsEarlierUsersAndYieldsToLegacyWritesBeforeRetry() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var users = new[] { await InsertLegacyUser(db), await InsertLegacyUser(db), await InsertLegacyUser(db) }
                .Order().ToArray();
            foreach (var user in users) await InsertLegacyAward(db, user);
            await db.GetService<IMigrator>().MigrateAsync();
            var slow = new SlowBackfillInterceptor();
            await using var migrating = MigrationContext(db, slow);
            var backfill = new BoundedReadStateBackfill(migrating, Substitute.For<IUserDateService>()).RunAsync();
            await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Exception? failure;
            try
            {
                (await db.Users.Where(user => user.Id == users[0])
                    .Select(user => EF.Property<bool?>(user, XpHistoryStore.InitializedProperty)).SingleAsync()).Should().BeTrue();
                db.Database.SetCommandTimeout(3);
                var unrelatedWrite = () => InsertLegacyAward(db, users[2]);
                await unrelatedWrite.Should().NotThrowAsync("one user's backfill must not hold a table lock over another user's writes");
                db.Database.SetCommandTimeout(7);
                var queuedWrite = () => InsertLegacyAward(db, users[1]);
                await queuedWrite.Should().NotThrowAsync("a user backfill must yield before an old writer times out");
            }
            finally
            {
                failure = await Record.ExceptionAsync(() => backfill);
            }

            (failure?.GetBaseException()).Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("57014");
            (await db.Users.Where(user => user.Id == users[1])
                .Select(user => EF.Property<bool?>(user, XpHistoryStore.InitializedProperty)).SingleAsync()).Should().BeNull();
            (await new XpHistoryStore(db).ReadBaselineAsync(users[1], new DateOnly(2026, 6, 2))).Should().Be(14);
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET statement_timeout = '12s'; SET lock_timeout = '2s';");
            await new BoundedReadStateBackfill(db, Substitute.For<IUserDateService>()).RunAsync();
            (await db.XpDailyTotals.Where(total => total.UserId == users[1]).Select(total => total.CumulativeXp).SingleAsync()).Should().Be(14);
            (await db.Users.CountAsync(user => EF.Property<bool?>(user, XpHistoryStore.InitializedProperty) == true)).Should().Be(3);
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
        var email = $"migration-lock-{userId:N}@example.test";
        var atUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "Name", "Email", "CreatedAtUtc")
            VALUES ({userId}, 'Legacy', {email}, {atUtc})
            """);
        return userId;
    }

    private sealed class SlowBackfillInterceptor : DbCommandInterceptor
    {
        private int userBatches;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("WITH daily AS", StringComparison.Ordinal) && ++userBatches == 2)
            {
                command.CommandText = "SELECT pg_sleep(8);\n" + command.CommandText;
                Started.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
    }
}
