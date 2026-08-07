using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AddHabitScheduledStartDateMigrationTests
{
    [Fact]
    public void Up_SeedsAmbiguousLegacyRowsFromCurrentDueDate()
    {
        var migration = new AddHabitScheduledStartDate();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddHabitScheduledStartDate)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var operation = builder.Operations.OfType<SqlOperation>().Should().ContainSingle().Subject;

        operation.Sql.Should().Contain("SET \"ScheduledStartDate\" = \"DueDate\"");
        operation.Sql.Should().Contain("WHERE \"ScheduledStartDate\" IS NULL");
    }
}
