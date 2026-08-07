using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AddHabitScheduledStartDateMigrationTests
{
    [Fact]
    public void Up_LeavesAmbiguousLegacyRowsNullable()
    {
        var migration = new AddHabitScheduledStartDate();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddHabitScheduledStartDate)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        var operation = builder.Operations.OfType<AddColumnOperation>().Should().ContainSingle().Subject;

        operation.Name.Should().Be("ScheduledStartDate");
        operation.IsNullable.Should().BeTrue();
        builder.Operations.Should().NotContain(operation => operation is SqlOperation);
    }
}
