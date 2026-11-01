using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class CompleteGeneralTasksMigrationTests
{
    [Fact]
    public void Up_CompletesOnlyLoggedTasksWithoutALaterUnlog_AndDownRestoresExactlyThoseRows()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
            CREATE TABLE "Habits" (
                "Id" TEXT PRIMARY KEY, "Schedule" INTEGER, "IsCompleted" INTEGER,
                "IsDeleted" INTEGER, "RepeatIntervalMinutes" INTEGER, "Title" TEXT, "UpdatedAtUtc" TEXT);
            CREATE TABLE "HabitLogs" (
                "HabitId" TEXT, "Value" INTEGER, "IsDeleted" INTEGER,
                "CreatedAtUtc" TEXT, "DeletedAtUtc" TEXT);
            INSERT INTO "Habits" VALUES
                ('past', 3, 0, 0, NULL, 'Keep title', '2026-04-01T00:00:00Z'),
                ('repeat', 3, 0, 0, 480, 'Repeat', '2026-04-01T00:00:00Z'),
                ('unlogged', 3, 0, 0, NULL, 'Unlogged', '2026-04-01T00:00:00Z'),
                ('reopened', 3, 0, 0, NULL, 'Older active log', '2026-04-01T00:00:00Z'),
                ('relogged', 3, 0, 0, NULL, 'Log after unlog', '2026-04-01T00:00:00Z'),
                ('empty', 3, 0, 0, NULL, 'No logs', '2026-04-01T00:00:00Z'),
                ('skipped', 3, 0, 0, NULL, 'Zero log', '2026-04-01T00:00:00Z'),
                ('completed', 3, 1, 0, NULL, 'Already completed', '2026-04-01T00:00:00Z'),
                ('dated', 0, 0, 0, NULL, 'Dated', '2026-04-01T00:00:00Z'),
                ('recurring', 1, 0, 0, NULL, 'Recurring', '2026-04-01T00:00:00Z'),
                ('deleted', 3, 0, 1, NULL, 'Deleted', '2026-04-01T00:00:00Z');
            INSERT INTO "HabitLogs" VALUES
                ('past', 1, 0, '2026-04-01T08:00:00Z', NULL),
                ('repeat', 1, 0, '2026-04-01T08:00:00Z', NULL),
                ('unlogged', 1, 1, '2026-04-01T08:00:00Z', '2026-04-01T09:00:00Z'),
                ('reopened', 1, 0, '2026-04-01T07:00:00Z', NULL),
                ('reopened', 1, 1, '2026-04-01T08:00:00Z', '2026-04-01T09:00:00Z'),
                ('relogged', 1, 1, '2026-04-01T08:00:00Z', '2026-04-01T09:00:00Z'),
                ('relogged', 1, 0, '2026-04-01T10:00:00Z', NULL),
                ('skipped', 0, 0, '2026-04-01T08:00:00Z', NULL),
                ('completed', 1, 0, '2026-04-01T08:00:00Z', NULL),
                ('dated', 1, 0, '2026-04-01T08:00:00Z', NULL),
                ('recurring', 1, 0, '2026-04-01T08:00:00Z', NULL),
                ('deleted', 1, 0, '2026-04-01T08:00:00Z', NULL);
            """);
        var original = ReadRows(connection);
        var up = Operations(up: true);

        foreach (var operation in up) Execute(connection, operation.Sql);
        var migrated = ReadRows(connection);
        migrated.Where(pair => pair.Value != original[pair.Key]).Select(pair => pair.Key)
            .Should().BeEquivalentTo("past", "repeat", "relogged", "deleted");
        foreach (var id in new[] { "past", "repeat", "relogged", "deleted" })
            migrated[id].Should().Be(original[id] with { IsCompleted = true });

        foreach (var operation in up) Execute(connection, operation.Sql);
        ReadRows(connection).Should().BeEquivalentTo(migrated);
        foreach (var operation in Operations(up: false)) Execute(connection, operation.Sql);
        ReadRows(connection).Should().BeEquivalentTo(original);
        foreach (var operation in Operations(up: false)) Execute(connection, operation.Sql);
        ReadRows(connection).Should().BeEquivalentTo(original);
    }

    private sealed record Row(int Schedule, bool IsCompleted, bool IsDeleted, long? RepeatIntervalMinutes,
        string Title, string UpdatedAtUtc);

    private static Dictionary<string, Row> ReadRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM \"Habits\"";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, Row>();
        while (reader.Read())
            rows.Add(reader.GetString(0), new(reader.GetInt32(1), reader.GetBoolean(2), reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetString(5), reader.GetString(6)));
        return rows;
    }

    private static SqlOperation[] Operations(bool up)
    {
        var type = typeof(OrbitDbContext).Assembly.GetTypes().SingleOrDefault(type => type.Name == "CompleteGeneralTasks");
        type.Should().NotBeNull("the General task completion backfill must be registered");
        var migration = (Migration)Activator.CreateInstance(type!)!;
        return (up ? migration.UpOperations : migration.DownOperations).Cast<SqlOperation>().ToArray();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
