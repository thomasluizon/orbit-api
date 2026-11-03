using System.Data.Common;
using System.Reflection;
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
    private const string MigrationId = "20261102214043_CompleteDurableAuthAndDeliveryState";

    [Fact]
    public void BranchMigrations_FollowTheMergedBaseSchema()
    {
        var baseId = typeof(CompleteBoundedReadIndexes).GetCustomAttribute<MigrationAttribute>()!.Id;
        foreach (var type in new[] { typeof(CompleteDurableAuthAndDeliveryState) })
            string.CompareOrdinal(type.GetCustomAttribute<MigrationAttribute>()!.Id, baseId).Should().BePositive();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("20261102193912_CompleteBoundedReadIndexes")]
    public void MergedMigrations_GenerateInTimestampOrderFromEmptyAndMainSchemas(string fromMigration)
    {
        using var db = Context("Host=unused;Database=unused");
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys
            .Where(id => string.CompareOrdinal(id, fromMigration) > 0).ToArray();
        migrations.Should().BeInAscendingOrder(StringComparer.Ordinal);
        migrations.Should().ContainInOrder(MigrationId, "20261102220044_UnmapMarketingBroadcastDeliveries");
        var script = db.GetService<IMigrator>().GenerateScript(fromMigration);
        var previous = -1;
        foreach (var migration in migrations)
        {
            var history = script.IndexOf($"VALUES ('{migration}'", StringComparison.Ordinal);
            history.Should().BeGreaterThan(previous);
            previous = history;
        }
        script.Should().Contain("CREATE TABLE IF NOT EXISTS \"EmailChallenges\"");
        if (fromMigration == "0")
            script.IndexOf("CREATE TABLE \"XpDailyTotals\"", StringComparison.Ordinal)
                .Should().BeLessThan(script.IndexOf("CREATE TABLE IF NOT EXISTS \"EmailChallenges\"", StringComparison.Ordinal));
        else
            script.Should().NotContain("CREATE TABLE \"XpDailyTotals\"");
    }

    [Fact]
    public void SchemaAdditions_AreReplaySafeBeforeConcurrentIndexes()
    {
        using var db = Context("Host=unused;Database=unused");
        var commands = db.GetService<IMigrationsSqlGenerator>().Generate(new CompleteDurableAuthAndDeliveryState().UpOperations);
        commands.Should().Contain(command => !command.TransactionSuppressed);
        foreach (var command in commands.Where(command => !command.TransactionSuppressed))
        {
            if (command.CommandText.Contains("ALTER TABLE", StringComparison.Ordinal))
                command.CommandText.Should().Contain("ADD COLUMN IF NOT EXISTS");
            if (command.CommandText.Contains("CREATE TABLE", StringComparison.Ordinal))
                command.CommandText.Should().Contain("CREATE TABLE IF NOT EXISTS");
        }
    }

    [Theory]
    [InlineData(typeof(CompleteDurableAuthAndDeliveryState))]
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
    public Task Dispatch_RestartsAfterSchemaCommit() => Restart(true, false, false);

    [PostgresFact]
    public Task Dispatch_RestartsBeforeHistoryInsert() => Restart(true, true, false);

    [PostgresFact]
    public Task Dispatch_ReplacesInvalidIndex() => Restart(true, false, true);

    [PostgresFact]
    public Task DurableState_RestartsAfterSchemaCommit() => Restart(false, false, false);

    [PostgresFact]
    public Task DurableState_RestartsBeforeHistoryInsert() => Restart(false, true, false);

    [PostgresFact]
    public Task DurableState_ReplacesInvalidIndex() => Restart(false, false, true);

    private static Task Restart(bool dispatch, bool beforeHistory, bool invalidIndex)
        => PostgresTestDatabase.WithDatabase(async db =>
        {
            var connection = db.Database.GetConnectionString()!;
            await using (var interrupted = Context(connection, new StopMigration(beforeHistory)))
            {
                var migrate = () => interrupted.GetService<IMigrator>().MigrateAsync(MigrationId);
                await migrate.Should().ThrowAsync<MigrationInterruptedException>();
            }
            (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(MigrationId);
            var column = dispatch ? "BackgroundJobId" : "PushBody";
            (await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE column_name = {0}", column)
                .SingleAsync()).Should().Be(1, "the schema step committed before the simulated process stop");

            var index = dispatch ? "IX_MarketingDeliveries_SentAtUtc_NextRecoveryAtUtc"
                : "IX_MarketingDeliveries_BroadcastId_RecipientId";
            if (invalidIndex)
            {
                var dispatchValues = dispatch ? ", \"BackgroundJobId\", \"NextRecoveryAtUtc\"" : "";
                var dispatchData = dispatch ? ", NULL, now()" : "";
                var broadcast = dispatch ? "gen_random_uuid()" : "'00000000-0000-0000-0000-000000000001'::uuid";
                var seed = $"""
                    INSERT INTO "MarketingDeliveries" ("Id", "BroadcastId", "RecipientId", "Email", "Language",
                        "SubjectEn", "SubjectPt", "BodyHtmlEn", "BodyHtmlPt", "NotBeforeAtUtc", "SentAtUtc"{dispatchValues})
                    SELECT gen_random_uuid(), {broadcast},
                        '00000000-0000-0000-0000-000000000002'::uuid, 'smoke@example.test', 'en',
                        'subject', 'subject', 'body', 'body', now(), now(){dispatchData} FROM generate_series(1, 2);
                    """;
                await db.Database.ExecuteSqlRawAsync(seed);
                var columns = dispatch ? "\"SentAtUtc\", \"NextRecoveryAtUtc\"" : "\"BroadcastId\", \"RecipientId\"";
                var buildSql = $"CREATE UNIQUE INDEX CONCURRENTLY \"{index}\" ON \"MarketingDeliveries\" ({columns});";
                var build = () => db.Database.ExecuteSqlRawAsync(buildSql);
                await build.Should().ThrowAsync<Npgsql.PostgresException>().Where(exception => exception.SqlState == "23505");
                (await IndexValid(db, index)).Should().BeFalse();
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MarketingDeliveries\"");
            }

            await using var restarted = Context(connection);
            await restarted.GetService<IMigrator>().MigrateAsync(MigrationId);
            (await restarted.Database.GetAppliedMigrationsAsync()).Should().Contain(MigrationId);
            (await IndexValid(restarted, index)).Should().BeTrue();
            await restarted.Database.ExecuteSqlRawAsync("""
                INSERT INTO "MarketingDeliveries" ("Id", "BroadcastId", "RecipientId", "Email", "Language",
                    "SubjectEn", "SubjectPt", "BodyHtmlEn", "BodyHtmlPt", "NotBeforeAtUtc")
                VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'recovered@example.test', 'en',
                    'subject', 'subject', 'body', 'body', now());
                """);
        }, "20261102193912_CompleteBoundedReadIndexes");

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
