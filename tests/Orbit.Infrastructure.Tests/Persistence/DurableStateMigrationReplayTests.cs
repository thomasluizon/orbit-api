using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class DurableStateMigrationReplayTests
{
    private const string DurableId = "20261102083947_CompleteDurableAuthAndDeliveryState";
    private const string DispatchId = "20261102123116_TrackMarketingDispatch";

    [Fact]
    public void EmailChallenges_HasNoIndependentlyCommittedOperations()
    {
        using var db = Context("Host=unused;Database=unused");
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(new PersistEmailChallenges().UpOperations);
        commands.Should().NotBeEmpty().And.OnlyContain(command => !command.TransactionSuppressed);
    }

    [Theory]
    [InlineData(typeof(CompleteDurableAuthAndDeliveryState))]
    [InlineData(typeof(TrackMarketingDispatch))]
    public void ConcurrentMigrations_UseReplaySafeSchemaAndRebuildInterruptedIndexes(Type type)
    {
        var migration = (Migration)Activator.CreateInstance(type)!;
        using var db = Context("Host=unused;Database=unused");
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations);
        foreach (var command in commands.Where(command => !command.TransactionSuppressed))
        {
            if (command.CommandText.Contains("ADD", StringComparison.Ordinal))
                command.CommandText.Should().Contain("ADD COLUMN IF NOT EXISTS");
            if (command.CommandText.Contains("CREATE TABLE", StringComparison.Ordinal))
                command.CommandText.Should().Contain("CREATE TABLE IF NOT EXISTS");
        }
        var concurrent = commands.Where(command => command.CommandText.Contains("CREATE", StringComparison.Ordinal)
            && command.CommandText.Contains("INDEX CONCURRENTLY", StringComparison.Ordinal)).ToArray();
        concurrent.Should().NotBeEmpty();
        foreach (var command in concurrent)
        {
            command.TransactionSuppressed.Should().BeTrue();
            command.CommandText.Should().Contain("INDEX CONCURRENTLY IF NOT EXISTS");
            var name = command.CommandText.Split('"')[1];
            commands.TakeWhile(item => item != command).Should().Contain(item => item.TransactionSuppressed
                && item.CommandText.Contains($"DROP INDEX CONCURRENTLY IF EXISTS \"{name}\"", StringComparison.Ordinal));
        }
    }

    [PostgresFact]
    public Task Dispatch_RestartsAfterSchemaCommit() => Restart(DispatchId, false, false);

    [PostgresFact]
    public Task Dispatch_RestartsBeforeHistoryInsert() => Restart(DispatchId, true, false);

    [PostgresFact]
    public Task Dispatch_ReplacesInvalidIndex() => Restart(DispatchId, false, true);

    [PostgresFact]
    public Task DurableState_RestartsAfterSchemaCommit() => Restart(DurableId, false, false);

    [PostgresFact]
    public Task DurableState_RestartsBeforeHistoryInsert() => Restart(DurableId, true, false);

    [PostgresFact]
    public Task DurableState_ReplacesInvalidIndex() => Restart(DurableId, false, true);

    private static Task Restart(string target, bool beforeHistory, bool invalidIndex)
        => PostgresTestDatabase.WithDatabase(async db =>
        {
            var connection = db.Database.GetConnectionString()!;
            await using (var interrupted = Context(connection, new StopMigration(beforeHistory)))
            {
                var migrate = () => interrupted.GetService<IMigrator>().MigrateAsync(target);
                await migrate.Should().ThrowAsync<MigrationInterruptedException>();
            }
            (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(target);
            var column = target == DispatchId ? "BackgroundJobId" : "PushBody";
            (await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE column_name = {0}", column)
                .SingleAsync()).Should().Be(1, "the schema step committed before the simulated process stop");

            var index = target == DispatchId ? "IX_MarketingDeliveries_SentAtUtc_NextRecoveryAtUtc"
                : "IX_MarketingDeliveries_BroadcastId_RecipientId";
            if (invalidIndex)
            {
                var dispatchValues = target == DispatchId ? ", \"BackgroundJobId\", \"NextRecoveryAtUtc\"" : "";
                var dispatchData = target == DispatchId ? ", NULL, now()" : "";
                var broadcast = target == DispatchId ? "gen_random_uuid()" : "'00000000-0000-0000-0000-000000000001'::uuid";
                var seed = $"""
                    INSERT INTO "MarketingDeliveries" ("Id", "BroadcastId", "RecipientId", "Email", "Language",
                        "SubjectEn", "SubjectPt", "BodyHtmlEn", "BodyHtmlPt", "NotBeforeAtUtc", "SentAtUtc"{dispatchValues})
                    SELECT gen_random_uuid(), {broadcast},
                        '00000000-0000-0000-0000-000000000002'::uuid, 'smoke@example.test', 'en',
                        'subject', 'subject', 'body', 'body', now(), now(){dispatchData} FROM generate_series(1, 2);
                    """;
                await db.Database.ExecuteSqlRawAsync(seed);
                var columns = target == DispatchId ? "\"SentAtUtc\", \"NextRecoveryAtUtc\"" : "\"BroadcastId\", \"RecipientId\"";
                var buildSql = $"CREATE UNIQUE INDEX CONCURRENTLY \"{index}\" ON \"MarketingDeliveries\" ({columns});";
                var build = () => db.Database.ExecuteSqlRawAsync(buildSql);
                await build.Should().ThrowAsync<Npgsql.PostgresException>().Where(exception => exception.SqlState == "23505");
                (await IndexValid(db, index)).Should().BeFalse();
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MarketingDeliveries\"");
            }

            await using var restarted = Context(connection);
            await restarted.GetService<IMigrator>().MigrateAsync(target);
            (await restarted.Database.GetAppliedMigrationsAsync()).Should().Contain(target);
            (await IndexValid(restarted, index)).Should().BeTrue();
            await restarted.Database.ExecuteSqlRawAsync("""
                INSERT INTO "MarketingDeliveries" ("Id", "BroadcastId", "RecipientId", "Email", "Language",
                    "SubjectEn", "SubjectPt", "BodyHtmlEn", "BodyHtmlPt", "NotBeforeAtUtc")
                VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'recovered@example.test', 'en',
                    'subject', 'subject', 'body', 'body', now());
                """);
        }, target == DispatchId ? "20261102102120_PersistStripeCustomerErasureRecovery" : "20261102081729_AddReminderScheduleProbes");

    private static Task<bool> IndexValid(OrbitDbContext db, string name)
        => db.Database.SqlQueryRaw<bool>("""
            SELECT i.indisvalid AS "Value" FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE c.relname = {0}
            """, name).SingleAsync();

    private static OrbitDbContext Context(string connection, DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>().UseNpgsql(connection);
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new OrbitDbContext(options.Options);
    }

    private sealed class MigrationInterruptedException : Exception;

    private sealed class StopMigration(bool beforeHistory) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var stop = beforeHistory ? command.CommandText.Contains("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal)
                : command.CommandText.Contains("CREATE", StringComparison.Ordinal)
                  && command.CommandText.Contains("INDEX CONCURRENTLY", StringComparison.Ordinal);
            if (stop)
                throw new MigrationInterruptedException();
            return ValueTask.FromResult(result);
        }
    }
}
