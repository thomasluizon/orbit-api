using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class SeededDataMigrationTests
{
    private static async Task Apply(OrbitDbContext db, Migration migration, bool up = true)
    {
        migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
        foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(up ? migration.UpOperations : migration.DownOperations))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
    }
    private static async Task<string[]> Rows(OrbitDbContext db, string expression, string table)
    {
        var sql = $"SELECT {expression} AS \"Value\" FROM \"{table}\" ORDER BY \"Id\"";
        return await db.Database.SqlQueryRaw<string>(sql).ToArrayAsync();
    }

    [PostgresFact]
    public async Task RecordAiChatTokenUsage_Down_SumsAndRemovesExtraRows()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "AiUsageDaily" ("Id" int PRIMARY KEY, "Date" date, "Model" text, "Purpose" text,
                    "Calls" bigint, "CachedTokens" bigint, "PromptTokens" bigint, "CompletionTokens" bigint, "TotalTokens" bigint, "CostUsd" numeric);
                CREATE UNIQUE INDEX "IX_AiUsageDaily_Date_Model_Purpose" ON "AiUsageDaily" ("Date", "Model", "Purpose");
                """);
            await Apply(db, new RecordAstraChatTokenUsage());
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "AiUsageDaily" VALUES
                    (1, '2026-03-31', 'model', 'chat', 1, 2, 3, 4, 5, 0.1, '00000000-0000-0000-0000-000000000001'),
                    (2, '2026-03-31', 'model', 'chat', 10, 20, 30, 40, 50, 0.2, '00000000-0000-0000-0000-000000000002'),
                    (3, '2026-03-31', 'model', 'chat', 100, 200, 300, 400, 500, 0.3, NULL),
                    (4, '2026-03-31', 'other', 'chat', 9, 8, 7, 6, 5, 1, NULL);
                """);
            await Apply(db, new RecordAstraChatTokenUsage(), false);
            (await Rows(db, "concat_ws(':', \"Id\", \"Calls\", \"CachedTokens\", \"PromptTokens\", \"CompletionTokens\", \"TotalTokens\", \"CostUsd\")", "AiUsageDaily"))
                .Should().Equal("1:111:222:333:444:555:0.6", "4:9:8:7:6:5:1");
        }, "0");
    }

    [PostgresFact]
    public async Task SyncChangesIndexes_ExistAndAreUsed()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            var indexes = new[] { ("Tags", "UserId"), ("Notifications", "UserId"), ("Habits", "UserId"), ("HabitLogs", "HabitId"), ("Goals", "UserId"), ("GoalProgressLogs", "GoalId"), ("ChecklistTemplates", "UserId") };
            foreach (var (table, owner) in indexes)
            {
                var schemaSql = $"""
                    CREATE TABLE "{table}" ("Id" int PRIMARY KEY, "{owner}" int, "UpdatedAtUtc" timestamptz);
                    INSERT INTO "{table}" SELECT n, n % 1000, '2026-03-31'::timestamptz + n * INTERVAL '1 second' FROM generate_series(1, 10000) n;
                    """;
                await db.Database.ExecuteSqlRawAsync(schemaSql);
            }
            await Apply(db, new AddSyncChangesV2Indexes());
            await Apply(db, new AddSyncChangesV2Indexes());
            var names = await db.Database.SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'").ToArrayAsync();
            foreach (var (table, owner) in indexes)
            {
                var index = $"IX_{table}_{owner}_UpdatedAtUtc";
                names.Should().Contain(index);
                var analyzeSql = $"ANALYZE \"{table}\"";
                await db.Database.ExecuteSqlRawAsync(analyzeSql);
                var explainSql = $"EXPLAIN SELECT * FROM \"{table}\" WHERE \"{owner}\" = 1 AND \"UpdatedAtUtc\" > '2026-03-31T01:00:00Z'";
                var plan = await db.Database.SqlQueryRaw<string>(explainSql).ToArrayAsync();
                string.Join('\n', plan).Should().Contain(index);
            }
        }, "0");
    }

    [PostgresFact]
    public async Task CompleteGeneralTasks_OnPostgres_CompletesOnlyLoggedTasksAndRollbackRestoresOnlyBackfill()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Habits" ("Id" uuid PRIMARY KEY, "Schedule" int, "IsCompleted" bool);
                CREATE TABLE "HabitLogs" ("HabitId" uuid, "Value" int, "IsDeleted" bool, "CreatedAtUtc" timestamptz, "DeletedAtUtc" timestamptz);
                INSERT INTO "Habits" SELECT ('00000000-0000-0000-0000-' || lpad(n::text, 12, '0'))::uuid, CASE WHEN n = 6 THEN 1 ELSE 3 END, n = 5 FROM generate_series(1, 7) n;
                INSERT INTO "HabitLogs" VALUES
                    ('00000000-0000-0000-0000-000000000001', 1, false, '2026-03-31T08:00Z', NULL),
                    ('00000000-0000-0000-0000-000000000002', 1, false, '2026-03-31T08:00Z', NULL),
                    ('00000000-0000-0000-0000-000000000002', 1, true, '2026-03-31T08:00Z', '2026-03-31T09:00Z'),
                    ('00000000-0000-0000-0000-000000000003', 0, false, '2026-03-31T08:00Z', NULL),
                    ('00000000-0000-0000-0000-000000000004', 1, true, '2026-03-31T08:00Z', '2026-03-31T09:00Z'),
                    ('00000000-0000-0000-0000-000000000004', 1, false, '2026-03-31T10:00Z', NULL),
                    ('00000000-0000-0000-0000-000000000005', 1, false, '2026-03-31T08:00Z', NULL),
                    ('00000000-0000-0000-0000-000000000006', 1, false, '2026-03-31T08:00Z', NULL);
                """);
            var original = await Rows(db, "\"IsCompleted\"::text", "Habits");
            await Apply(db, new CompleteGeneralTasks());
            await Apply(db, new CompleteGeneralTasks());
            (await Rows(db, "\"IsCompleted\"::text", "Habits")).Should().Equal("true", "false", "false", "true", "true", "false", "false");
            await Apply(db, new CompleteGeneralTasks(), false);
            (await Rows(db, "\"IsCompleted\"::text", "Habits")).Should().Equal(original);
        }, "0");
    }

    [PostgresFact]
    public async Task HealHabitModelData_OnPostgres_ClearsOnlyGeneralEndDates()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Habits" ("Id" int PRIMARY KEY, "IsGeneral" bool, "EndDate" date);
                INSERT INTO "Habits" VALUES (1, true, '2026-03-31'), (2, false, '2026-03-31'), (3, true, NULL);
                """);
            await Apply(db, new HealHabitModelData());
            await Apply(db, new HealHabitModelData());
            (await Rows(db, "coalesce(\"EndDate\"::text, 'null')", "Habits")).Should().Equal("null", "2026-03-31", "null");
            await Apply(db, new HealHabitModelData(), false);
            (await Rows(db, "coalesce(\"EndDate\"::text, 'null')", "Habits")).Should().Equal("null", "2026-03-31", "null");
        }, "0");
    }

    [PostgresFact]
    public async Task ExpandHabitNextDueDate_CopiesDueDateForEveryRow()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Habits" ("Id" int PRIMARY KEY, "DueDate" date NOT NULL);
                INSERT INTO "Habits" VALUES (1, '2026-03-31'), (2, '2026-04-30'), (3, '0001-01-01');
                """);
            await Apply(db, new ExpandHabitNextDueDate());
            (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"Habits\" WHERE \"NextDueDate\" = \"DueDate\"").SingleAsync()).Should().Be(3);
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Habits\" SET \"DueDate\" = '2026-05-31' WHERE \"Id\" = 1");
            (await Rows(db, "\"NextDueDate\"::text", "Habits")).Should().Equal("2026-05-31", "2026-04-30", "0001-01-01");
        }, "0");
    }

    [PostgresFact]
    public async Task HardenModelConstraints_RemovesOnlyOrphansAndAddsCascadeKeys()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Users" ("Id" int PRIMARY KEY, "TimeZone" text, "ThemePreference" text, "Language" text, "ColorScheme" text);
                CREATE TABLE "Habits" ("Id" int PRIMARY KEY, "UserId" int);
                CREATE TABLE "Tags" ("Id" int PRIMARY KEY, "UserId" int, "Name" text NOT NULL, "Color" text NOT NULL, "IsDeleted" bool);
                CREATE TABLE "Goals" ("Id" int PRIMARY KEY, "Unit" text NOT NULL);
                INSERT INTO "Users" VALUES (1, 'UTC', 'system', 'en', 'default');
                INSERT INTO "Habits" VALUES (1, 1), (2, 99);
                INSERT INTO "Tags" VALUES (1, 1, 'Owned', 'red', false), (2, 99, 'Orphan', 'blue', false);
                """);
            await Apply(db, new HardenModelConstraints());
            (await Rows(db, "\"Id\"::text", "Habits")).Should().Equal("1");
            (await Rows(db, "\"Id\"::text", "Tags")).Should().Equal("1");
            var keys = await db.Database.SqlQueryRaw<string>("SELECT conname || ':' || confdeltype::text AS \"Value\" FROM pg_constraint WHERE conname IN ('FK_Habits_Users_UserId', 'FK_Tags_Users_UserId') ORDER BY conname").ToArrayAsync();
            keys.Should().Equal("FK_Habits_Users_UserId:c", "FK_Tags_Users_UserId:c");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Users\" WHERE \"Id\" = 1");
            (await Rows(db, "\"Id\"::text", "Habits")).Should().BeEmpty();
            (await Rows(db, "\"Id\"::text", "Tags")).Should().BeEmpty();
        }, "0");
    }

    [PostgresFact]
    public async Task ClearGeneralHabitCompletionFlags_UpChangesOnlyGeneralCompleted_DownRestoresOnlyLoggedRows()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Habits" ("Id" int PRIMARY KEY, "IsGeneral" bool, "IsCompleted" bool);
                CREATE TABLE "HabitLogs" ("HabitId" int, "Value" int, "IsDeleted" bool);
                INSERT INTO "Habits" VALUES (1, true, true), (2, false, true), (3, true, false), (4, true, true), (5, true, true);
                INSERT INTO "HabitLogs" VALUES (1, 1, false), (3, 0, false), (4, 1, true);
                """);
            await Apply(db, new ClearGeneralHabitCompletionFlags());
            (await Rows(db, "\"IsCompleted\"::text", "Habits")).Should().Equal("false", "true", "false", "false", "false");
            await Apply(db, new ClearGeneralHabitCompletionFlags(), false);
            (await Rows(db, "\"IsCompleted\"::text", "Habits")).Should().Equal("true", "true", "false", "false", "false");
        }, "0");
    }

    [PostgresFact]
    public async Task BackfillOnboarding_FlipsOnlyUsersWithLiveHabit()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Users" ("Id" int PRIMARY KEY, "HasCompletedOnboarding" bool);
                CREATE TABLE "Habits" ("UserId" int, "DeletedAtUtc" timestamptz);
                INSERT INTO "Users" VALUES (1, false), (2, false), (3, false), (4, true);
                INSERT INTO "Habits" VALUES (1, NULL), (2, '2026-03-31');
                """);
            await Apply(db, new BackfillHasCompletedOnboardingForExistingUsers());
            (await Rows(db, "\"HasCompletedOnboarding\"::text", "Users")).Should().Equal("true", "false", "false", "true");
        }, "0");
    }

    [PostgresFact]
    public async Task NormalizeHabitPositions_RenumbersLiveRowsAndSkipsDeleted()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Habits" ("Id" int PRIMARY KEY, "UserId" int, "ParentHabitId" int, "Position" int, "CreatedAtUtc" timestamptz, "IsDeleted" bool);
                INSERT INTO "Habits" VALUES
                    (1, 1, NULL, 0, '2026-03-31', false), (2, 1, NULL, 5, '2026-03-31', false),
                    (3, 1, NULL, 9, '2026-03-31', false), (4, 1, NULL, NULL, '2026-03-31', false),
                    (5, 1, NULL, 8, '2026-03-31', true), (6, 1, 1, 9, '2026-03-31', false),
                    (7, 2, NULL, 5, '2026-03-31', false), (8, 2, NULL, 5, '2026-03-30', false);
                """);
            await Apply(db, new NormalizeHabitPositions());
            (await Rows(db, "\"Position\"::text", "Habits")).Should().Equal("0", "1", "2", "3", "8", "0", "1", "0");
        }, "0");
    }

    [PostgresFact]
    public async Task AuditCleanupColumns_BackfillsMonthlyAndYearlyOnly()
    {
        await PostgresTestDatabase.WithDatabase(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "Users" ("Id" int PRIMARY KEY);
                CREATE TABLE "Habits" ("Id" int PRIMARY KEY, "DueDate" date, "FrequencyUnit" int);
                INSERT INTO "Habits" VALUES (1, '2026-03-31', 2), (2, '2026-02-28', 3), (3, '2026-03-30', 0), (4, '2026-04-30', 2);
                """);
            await Apply(db, new AuditCleanupColumns());
            (await Rows(db, "coalesce(\"OriginalDayOfMonth\"::text, 'null')", "Habits")).Should().Equal("31", "28", "null", "30");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Habits\" SET \"OriginalDayOfMonth\" = 15 WHERE \"Id\" = 4");
            foreach (var operation in new AuditCleanupColumns().UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>())
                await db.Database.ExecuteSqlRawAsync(operation.Sql);
            (await Rows(db, "coalesce(\"OriginalDayOfMonth\"::text, 'null')", "Habits")).Should().Equal("31", "28", "null", "15");
        }, "0");
    }
}
