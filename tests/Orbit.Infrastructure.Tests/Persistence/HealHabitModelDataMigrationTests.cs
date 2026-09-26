using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HealHabitModelDataMigrationTests
{
    [Fact]
    public void Up_ClearsGeneralEndDatesWithoutChangingOtherRows()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, """
            CREATE TABLE "Habits" ("Id" INTEGER PRIMARY KEY, "IsGeneral" INTEGER NOT NULL, "EndDate" TEXT);
            INSERT INTO "Habits" ("Id", "IsGeneral", "EndDate") VALUES
                (1, 1, '2027-01-01'),
                (2, 0, '2027-01-02'),
                (3, 1, NULL);
            """);

        var sql = GetOperations("Up").OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
        Execute(connection, sql);
        Execute(connection, sql);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"Id\", \"EndDate\" FROM \"Habits\" ORDER BY \"Id\"";
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetInt32(0).Should().Be(1);
        reader.IsDBNull(1).Should().BeTrue();
        reader.Read().Should().BeTrue();
        reader.GetInt32(0).Should().Be(2);
        reader.GetString(1).Should().Be("2027-01-02");
        reader.Read().Should().BeTrue();
        reader.GetInt32(0).Should().Be(3);
        reader.IsDBNull(1).Should().BeTrue();
        reader.Read().Should().BeFalse();
    }

    [Fact]
    public void Down_DoesNotRecreateClearedDates()
    {
        GetOperations("Down").Should().BeEmpty();
    }

    private static IReadOnlyList<MigrationOperation> GetOperations(string methodName)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(HealHabitModelData).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new HealHabitModelData(), [builder]);
        return builder.Operations;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
