using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class RaiseHabitCeilingMigrationTests
{
    [Fact]
    public void Up_UpdatesHabitCeilingAndDescription()
    {
        var operation = GetUpdate("Up");

        operation.Table.Should().Be("AppConfigs");
        operation.KeyColumns.Should().Equal("Key");
        operation.KeyValues.Cast<object>().Should().Equal("FreeMaxHabits");
        operation.Columns.Should().Equal("Description", "Value");
        operation.Values.Cast<object>().Should().Equal(
            "Maximum live top-level habits per user as an abuse guard",
            "1000");
    }

    [Fact]
    public void Down_RestoresPreviousHabitCeilingAndDescription()
    {
        var operation = GetUpdate("Down");

        operation.Table.Should().Be("AppConfigs");
        operation.KeyColumns.Should().Equal("Key");
        operation.KeyValues.Cast<object>().Should().Equal("FreeMaxHabits");
        operation.Columns.Should().Equal("Description", "Value");
        operation.Values.Cast<object>().Should().Equal(
            "Maximum number of active habits for free plan users",
            "10");
    }

    private static UpdateDataOperation GetUpdate(string methodName)
    {
        var migration = new RaiseHabitCeilingToAbuseGuard();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(RaiseHabitCeilingToAbuseGuard)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        return builder.Operations.OfType<UpdateDataOperation>().Should().ContainSingle().Subject;
    }
}
