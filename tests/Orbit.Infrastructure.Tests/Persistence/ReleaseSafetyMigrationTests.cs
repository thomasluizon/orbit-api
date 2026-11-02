using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Application.Common;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ReleaseSafetyMigrationTests
{
    [Fact]
    public void Migrations_KeepModelUnchangedAndRollbackEmpty()
    {
        new ReleaseSafetyAppConfig().DownOperations.Should().BeEmpty();
        new DropGeneralTaskCompletionBackfill().DownOperations.Should().BeEmpty();
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [PostgresFact]
    public async Task ConfigSeed_OnPostgres_InsertsFiveAndPreservesOperatorValue()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "AppConfigs" ("Key" text PRIMARY KEY, "Value" text NOT NULL, "Description" text);
                """);
            await Apply(db, new ReleaseSafetyAppConfig());
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var config = new AppConfigService(db, cache);
            (await config.GetAsync(AppConfigKeys.MaxActiveApiKeys, 99)).Should().Be(5);
            (await config.GetAllAsync())[AppConfigKeys.MaxActiveApiKeys].Should().Be("5");
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "AppConfigs" SET "Value" = '3' WHERE "Key" = 'MaxActiveApiKeys';
                """);
            await Apply(db, new ReleaseSafetyAppConfig());
            cache.Clear();
            (await config.GetAsync(AppConfigKeys.MaxActiveApiKeys, 99)).Should().Be(3);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AppConfigs\"");
            cache.Clear();
            (await config.GetAsync(AppConfigKeys.MaxActiveApiKeys, 5)).Should().Be(5);
        }, "0");
    }

    [PostgresFact]
    public async Task LedgerDrop_OnPostgres_PreservesCompletionsThroughOriginalRollback()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "AppConfigs" ("Key" text PRIMARY KEY, "Value" text NOT NULL, "Description" text);
                CREATE TABLE "Habits" ("Id" uuid PRIMARY KEY, "IsCompleted" boolean NOT NULL);
                CREATE TABLE "GeneralTaskCompletionBackfill" ("HabitId" uuid PRIMARY KEY);
                INSERT INTO "Habits" VALUES
                    ('00000000-0000-0000-0000-000000000001', true),
                    ('00000000-0000-0000-0000-000000000002', false);
                INSERT INTO "GeneralTaskCompletionBackfill" VALUES ('00000000-0000-0000-0000-000000000001');
                """);
            await Apply(db, new ReleaseSafetyAppConfig());
            await Apply(db, new DropGeneralTaskCompletionBackfill());
            (await db.Database.SqlQueryRaw<bool>(
                "SELECT to_regclass('\"GeneralTaskCompletionBackfill\"') IS NULL AS \"Value\"").SingleAsync()).Should().BeTrue();
            await Apply(db, new DropGeneralTaskCompletionBackfill(), up: false);
            await Apply(db, new CompleteGeneralTasks(), up: false);
            (await db.Database.SqlQueryRaw<bool>(
                "SELECT \"IsCompleted\" AS \"Value\" FROM \"Habits\" ORDER BY \"Id\"").ToArrayAsync())
                .Should().Equal(true, false);
        }, "0");
    }

    private static async Task Apply(OrbitDbContext db, Migration migration, bool up = true)
    {
        foreach (var operation in (up ? migration.UpOperations : migration.DownOperations).Cast<SqlOperation>())
            await db.Database.ExecuteSqlRawAsync(operation.Sql);
    }
}
