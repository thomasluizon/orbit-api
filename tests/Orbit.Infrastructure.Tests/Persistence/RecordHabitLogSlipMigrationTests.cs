using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class RecordHabitLogSlipMigrationTests
{
    [Fact]
    public void Up_AddsNullableColumnWithoutDefault()
    {
        var column = GetOperations("Npgsql.EntityFrameworkCore.PostgreSQL")
            .OfType<AddColumnOperation>().Should().ContainSingle().Subject;

        column.Name.Should().Be("IsSlip");
        column.IsNullable.Should().BeTrue();
        column.DefaultValue.Should().BeNull();
        column.DefaultValueSql.Should().BeNull();
    }

    [Fact]
    public async Task OldWriter_InsertClassifiesPositiveBadHabitLogAsSlip()
    {
        using var connection = await CreateMigratedSqliteConnectionAsync();
        await InsertBadHabitLogWithoutSlipColumnAsync(connection);

        (await ReadIsSlipAsync(connection)).Should().BeTrue();
    }

    [Fact]
    public async Task OldWriter_ClassificationSurvivesHabitTypeChange()
    {
        using var connection = await CreateMigratedSqliteConnectionAsync();
        await InsertBadHabitLogWithoutSlipColumnAsync(connection);

        await ExecuteAsync(connection, "UPDATE \"Habits\" SET \"IsBadHabit\" = 0 WHERE \"Id\" = 'habit-1';");

        (await ReadIsSlipAsync(connection)).Should().BeTrue();
    }

    [Fact]
    public async Task NewWriter_ExplicitClassificationIsPreserved()
    {
        using var connection = await CreateMigratedSqliteConnectionAsync();
        await ExecuteAsync(connection, "INSERT INTO \"Habits\" (\"Id\", \"IsBadHabit\") VALUES ('habit-1', 1);");
        await ExecuteAsync(connection, "INSERT INTO \"HabitLogs\" (\"Id\", \"HabitId\", \"Value\", \"IsSlip\") VALUES ('log-1', 'habit-1', 1, 0);");

        (await ReadIsSlipAsync(connection)).Should().BeFalse();
    }

    private static IReadOnlyList<MigrationOperation> GetOperations(string provider)
    {
        var builder = new MigrationBuilder(provider);
        typeof(RecordHabitLogSlip).GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new RecordHabitLogSlip(), [builder]);
        return builder.Operations;
    }

    private static async Task<SqliteConnection> CreateMigratedSqliteConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE \"Habits\" (\"Id\" TEXT PRIMARY KEY, \"IsBadHabit\" BOOLEAN NOT NULL);");
        await ExecuteAsync(connection, "CREATE TABLE \"HabitLogs\" (\"Id\" TEXT PRIMARY KEY, \"HabitId\" TEXT NOT NULL, \"Value\" NUMERIC NOT NULL);");

        var operations = GetOperations("Microsoft.EntityFrameworkCore.Sqlite");
        var column = operations.OfType<AddColumnOperation>().Single();
        var nullable = column.IsNullable ? string.Empty : " NOT NULL";
        var defaultValue = column.DefaultValue is bool value ? $" DEFAULT {(value ? 1 : 0)}" : string.Empty;
        await ExecuteAsync(connection, $"ALTER TABLE \"HabitLogs\" ADD COLUMN \"{column.Name}\" BOOLEAN{nullable}{defaultValue};");
        foreach (var operation in operations.OfType<SqlOperation>())
        {
            if (operation.Sql.Contains("CREATE TRIGGER", StringComparison.Ordinal))
                await ExecuteAsync(connection, operation.Sql);
        }

        return connection;
    }

    private static async Task InsertBadHabitLogWithoutSlipColumnAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, "INSERT INTO \"Habits\" (\"Id\", \"IsBadHabit\") VALUES ('habit-1', 1);");
        await ExecuteAsync(connection, "INSERT INTO \"HabitLogs\" (\"Id\", \"HabitId\", \"Value\") VALUES ('log-1', 'habit-1', 1);");
    }

    private static async Task<bool?> ReadIsSlipAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"IsSlip\" FROM \"HabitLogs\" WHERE \"Id\" = 'log-1';";
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : Convert.ToBoolean(value);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
