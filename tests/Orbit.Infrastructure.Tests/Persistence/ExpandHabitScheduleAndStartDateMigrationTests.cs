using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ExpandHabitScheduleAndStartDateMigrationTests
{
    [Fact]
    public void TargetModel_PreservesRedesignWithConversationAndAiSecurityAdditions()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var target = context.GetService<IModelRuntimeInitializer>()
            .Initialize(Migration.TargetModel, designTime: true);
        var current = context.GetService<IDesignTimeModel>().Model;

        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(target.GetRelationalModel(), current.GetRelationalModel());
        differences.Should().HaveCount(15);
        RestoreProactivePreferenceMigrationTests.AssertRequiredPreference(differences);
        differences.OfType<CreateTableOperation>().Select(table => table.Name)
            .Should().BeEquivalentTo("ConversationTurns", "OAuthStates", "AiBudgetCalls");
        differences.OfType<CreateIndexOperation>().Should().HaveCount(7);
        differences.OfType<AddColumnOperation>().Should().HaveCount(2);
        differences.OfType<InsertDataOperation>().Should().HaveCount(2);
        AssertAiSecurityAdditions(differences);
        ConversationMigrationTests.AssertConversationExpansion(differences);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    internal static void AssertAiSecurityAdditions(IReadOnlyList<MigrationOperation> operations)
    {
        var oauth = operations.OfType<CreateTableOperation>().Should().ContainSingle(table => table.Name == "OAuthStates").Subject;
        oauth.Columns.Select(column => column.Name).Should().BeEquivalentTo(
            "Id", "SecretHash", "Kind", "Payload", "ExpiresAtUtc", "ConsumedAtUtc");
        oauth.Columns.Should().ContainSingle(column => column.Name == "ConsumedAtUtc"
            && column.ClrType == typeof(DateTime) && column.IsNullable);
        oauth.PrimaryKey!.Columns.Should().Equal("Id");
        oauth.ForeignKeys.Should().BeEmpty();
        var budget = operations.OfType<CreateTableOperation>().Should().ContainSingle(table => table.Name == "AiBudgetCalls").Subject;
        budget.Columns.Select(column => column.Name).Should().BeEquivalentTo(
            "Id", "UserId", "LocalDate", "ChargedTokens", "ChargedMicroDollars", "IsSettled");
        budget.Columns.Should().ContainSingle(column => column.Name == "ChargedMicroDollars"
            && column.ClrType == typeof(long) && !column.IsNullable && Equals(column.DefaultValue, 0L));
        budget.PrimaryKey!.Columns.Should().Equal("Id");
        var foreignKey = budget.ForeignKeys.Should().ContainSingle().Subject;
        foreignKey.Columns.Should().Equal("UserId");
        foreignKey.PrincipalTable.Should().Be("Users");
        foreignKey.PrincipalColumns.Should().Equal("Id");
        foreignKey.OnDelete.Should().Be(ReferentialAction.Cascade);
        operations.OfType<AddColumnOperation>().Should().ContainSingle(column => column.Table == "ApiKeys"
            && column.Name == "OAuthFamilyId" && column.ClrType == typeof(Guid) && column.IsNullable);
        var indexes = operations.OfType<CreateIndexOperation>()
            .Where(index => index.Table is "OAuthStates" or "AiBudgetCalls"
                || index.Table == "ApiKeys" && index.Name == "IX_ApiKeys_OAuthFamilyId").ToArray();
        indexes.Should().HaveCount(4);
        indexes.Should().ContainSingle(index => index.Name == "IX_OAuthStates_Kind_ExpiresAtUtc"
            && index.Table == "OAuthStates" && index.Columns.SequenceEqual(new[] { "Kind", "ExpiresAtUtc" }) && !index.IsUnique);
        indexes.Should().ContainSingle(index => index.Name == "IX_OAuthStates_SecretHash"
            && index.Table == "OAuthStates" && index.Columns.SequenceEqual(new[] { "SecretHash" }) && index.IsUnique);
        indexes.Should().ContainSingle(index => index.Name == "IX_AiBudgetCalls_UserId_LocalDate"
            && index.Table == "AiBudgetCalls" && index.Columns.SequenceEqual(new[] { "UserId", "LocalDate" }) && !index.IsUnique);
        indexes.Should().ContainSingle(index => index.Name == "IX_ApiKeys_OAuthFamilyId"
            && index.Table == "ApiKeys" && index.Columns.SequenceEqual(new[] { "OAuthFamilyId" }) && !index.IsUnique);
        var configs = operations.OfType<InsertDataOperation>().Should().ContainSingle(seed => seed.Table == "AppConfigs").Subject;
        var keyColumn = Array.IndexOf(configs.Columns, "Key");
        var valueColumn = Array.IndexOf(configs.Columns, "Value");
        var values = Enumerable.Range(0, configs.Values.GetLength(0))
            .ToDictionary(row => (string)configs.Values[row, keyColumn]!, row => (string)configs.Values[row, valueColumn]!);
        values.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["FreeAiCallsPerDay"] = "45",
            ["ProAiCallsPerDay"] = "450",
            ["FreeAiMicroDollarsPerDay"] = "20000",
            ["ProAiMicroDollarsPerDay"] = "120000"
        });
    }

    [Fact]
    public void Backfill_UsesEarliestActiveLogElseUtcCreationAndCapsAtDueDate()
    {
        using var connection = OpenDatabase();
        Execute(connection, """
            INSERT INTO "Habits" VALUES
                (1, 0, 0, 0, 0, 1, '[]', '2026-04-10', '2026-04-05T23:00:00Z', NULL, NULL, '2000-01-01T00:00:00Z'),
                (2, 0, 0, 0, 0, 1, '[]', '2026-04-10', '2026-04-05T23:00:00Z', NULL, NULL, '2000-01-01T00:00:00Z'),
                (3, 0, 0, 0, 0, 1, '[]', '2026-04-01', '2026-04-05T23:00:00Z', NULL, NULL, '2000-01-01T00:00:00Z'),
                (4, 0, 0, 0, 0, 1, '[]', '2026-04-01', '2026-04-05T23:00:00Z', NULL, NULL, '2000-01-01T00:00:00Z');
            INSERT INTO "HabitLogs" VALUES
                (1, '2026-04-03', 0), (1, '2026-04-02', 0), (1, '2026-04-01', 1),
                (2, '2026-04-01', 1), (4, '2026-04-04', 0);
            """);

        RunBackfill(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"StartDate\", \"UpdatedAtUtc\" FROM \"Habits\" ORDER BY \"Id\"";
        using var reader = command.ExecuteReader();
        foreach (var expected in new[] { "2026-04-02", "2026-04-05", "2026-04-01", "2026-04-01" })
        {
            reader.Read().Should().BeTrue();
            reader.GetString(0).Should().Be(expected);
            DateTime.Parse(reader.GetString(1)).Should().BeAfter(new DateTime(2000, 1, 2));
        }
        reader.Read().Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, null, HabitSchedule.OneTime)]
    [InlineData(false, false, 0, HabitSchedule.Recurring)]
    [InlineData(false, true, 1, HabitSchedule.Flexible)]
    [InlineData(true, false, null, HabitSchedule.General)]
    public void Backfill_DerivesEachSchedule(bool general, bool flexible, int? unit, HabitSchedule expected)
    {
        using var connection = OpenDatabase();
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO "Habits" VALUES
                (1, $general, $flexible, 0, $unit, $quantity, '[]', '2026-04-10', '2026-04-03T00:00:00Z', NULL, NULL, '2000-01-01T00:00:00Z');
            """;
        insert.Parameters.AddWithValue("$general", general);
        insert.Parameters.AddWithValue("$flexible", flexible);
        insert.Parameters.AddWithValue("$unit", (object?)unit ?? DBNull.Value);
        insert.Parameters.AddWithValue("$quantity", unit.HasValue ? 1 : DBNull.Value);
        insert.ExecuteNonQuery();

        RunBackfill(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Schedule\" FROM \"Habits\"";
        Convert.ToInt32(command.ExecuteScalar()).Should().Be((int)expected);
    }

    [Fact]
    public void Guard_CountsFourInvalidCombinationsBeforeAnyColumnWrites()
    {
        var operations = Migration.UpOperations;
        var guard = operations[0].Should().BeOfType<SqlOperation>().Subject.Sql;
        guard.Should().Contain("LOCK TABLE \"Habits\" IN SHARE ROW EXCLUSIVE MODE")
            .And.Contain("IF invalid_count > 0 THEN")
            .And.Contain("RAISE EXCEPTION 'Habit schedule expansion aborted: % invalid legacy rows', invalid_count");
        var query = guard[guard.IndexOf("SELECT COUNT(*)", StringComparison.Ordinal)..guard.IndexOf(';', guard.IndexOf("SELECT COUNT(*)", StringComparison.Ordinal))]
            .Replace(" INTO invalid_count", "", StringComparison.Ordinal)
            .Replace("cardinality(\"Days\")", "json_array_length(\"Days\")", StringComparison.Ordinal);
        using var connection = OpenDatabase();
        Execute(connection, """
            INSERT INTO "Habits" VALUES
                (1, 1, 0, 0, 0, 1, '[]', '2026-04-03', '2026-04-03', NULL, NULL, '2000-01-01T00:00:00Z'),
                (2, 1, 0, 1, NULL, NULL, '[]', '2026-04-03', '2026-04-03', NULL, NULL, '2000-01-01T00:00:00Z'),
                (3, 0, 1, 0, NULL, NULL, '[]', '2026-04-03', '2026-04-03', NULL, NULL, '2000-01-01T00:00:00Z'),
                (4, 0, 1, 0, 0, 1, '[1]', '2026-04-03', '2026-04-03', NULL, NULL, '2000-01-01T00:00:00Z'),
                (5, 1, 0, 0, NULL, NULL, '[]', '2026-04-03', '2026-04-03', NULL, NULL, '2000-01-01T00:00:00Z'),
                (6, 0, 0, 0, NULL, NULL, '[]', '2026-04-03', '2026-04-03', NULL, NULL, '2000-01-01T00:00:00Z'),
                (7, 0, 0, 0, 0, 1, '[]', '2026-04-03', '2026-04-03', NULL, NULL, '2000-01-01T00:00:00Z'),
                (8, 0, 1, 0, 1, 2, '[]', '2026-04-03', '2026-04-03', NULL, NULL, '2000-01-01T00:00:00Z');
            """);
        using var command = connection.CreateCommand();
        command.CommandText = query;
        Convert.ToInt32(command.ExecuteScalar()).Should().Be(4);
        Execute(connection, "DELETE FROM \"Habits\" WHERE \"Id\" <= 4");
        Convert.ToInt32(command.ExecuteScalar()).Should().Be(0);
    }

    [Fact]
    public void Expand_IsAdditiveAndSynchronizesLegacyWritersWithoutChangingTheAnchor()
    {
        RollingMigrationGate.Check("expand", Migration.UpOperations).Should().BeEmpty();
        var columns = Migration.UpOperations.OfType<AddColumnOperation>().ToList();
        columns.Select(column => column.Name).Should().BeEquivalentTo("StartDate", "Schedule");
        columns.Should().OnlyContain(column => !column.IsNullable && (column.DefaultValue != null || column.DefaultValueSql != null));
        var backfill = BackfillSql;
        backfill.Should().NotContain("ScheduledStartDate");
        var trigger = Migration.UpOperations.OfType<SqlOperation>().Last().Sql;
        trigger.Should().Contain("BEFORE INSERT OR UPDATE ON \"Habits\"")
            .And.Contain("NEW.\"Schedule\" := CASE")
            .And.Contain("NEW.\"StartDate\" := NEW.\"DueDate\"")
            .And.Contain("NEW.\"StartDate\" := OLD.\"StartDate\"")
            .And.Contain("RAISE EXCEPTION 'Invalid legacy habit schedule'");
    }

    [Fact]
    public void Down_RemovesOnlyExpandColumnsAndTrigger()
    {
        Migration.DownOperations.OfType<DropColumnOperation>().Select(column => column.Name)
            .Should().BeEquivalentTo("StartDate", "Schedule");
        Migration.DownOperations.OfType<SqlOperation>().Should().ContainSingle().Which.Sql
            .Should().Contain("DROP TRIGGER IF EXISTS").And.Contain("DROP FUNCTION IF EXISTS");
    }

    private static ExpandHabitScheduleAndStartDate Migration => new()
    {
        ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL"
    };

    private static string BackfillSql => Migration.UpOperations.OfType<SqlOperation>()
        .Single(operation => operation.Sql.StartsWith("UPDATE", StringComparison.Ordinal)).Sql;

    private static void RunBackfill(SqliteConnection connection) => Execute(connection,
        BackfillSql.Replace("LEAST(", "MIN(", StringComparison.Ordinal)
            .Replace(" AT TIME ZONE 'UTC'", "", StringComparison.Ordinal)
            .Replace("now()", "CURRENT_TIMESTAMP", StringComparison.Ordinal));

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
            CREATE TABLE "Habits" (
                "Id" INTEGER PRIMARY KEY, "IsGeneral" INTEGER, "IsFlexible" INTEGER, "IsBadHabit" INTEGER,
                "FrequencyUnit" INTEGER, "FrequencyQuantity" INTEGER, "Days" TEXT, "DueDate" TEXT,
                "CreatedAtUtc" TEXT, "StartDate" TEXT, "Schedule" INTEGER, "UpdatedAtUtc" TEXT);
            CREATE TABLE "HabitLogs" ("HabitId" INTEGER, "Date" TEXT, "IsDeleted" INTEGER);
            """);
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
