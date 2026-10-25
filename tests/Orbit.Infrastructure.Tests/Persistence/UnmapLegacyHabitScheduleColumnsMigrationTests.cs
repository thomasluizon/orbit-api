using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class UnmapLegacyHabitScheduleColumnsMigrationTests
{
    [Fact]
    public void Up_OnlyReplacesCompatibilityFunctionWithoutDroppingColumnsOrRewritingRows()
    {
        var migration = new UnmapLegacyHabitScheduleColumns();
        var operation = migration.UpOperations.Should().ContainSingle().Subject
            .Should().BeOfType<SqlOperation>().Subject;
        operation.SuppressTransaction.Should().BeFalse();
        operation.Sql.Should().StartWith("CREATE OR REPLACE FUNCTION sync_habit_schedule_expand()")
            .And.NotContain("UPDATE \"Habits\"")
            .And.NotContain("DROP ")
            .And.NotContain("NEW.\"Schedule\" :=");
    }

    [Fact]
    public void Down_RestoresThePreviousFunctionExactlyWithoutChangingStoredRows()
    {
        var operation = new UnmapLegacyHabitScheduleColumns().DownOperations.Should().ContainSingle().Subject
            .Should().BeOfType<SqlOperation>().Subject;
        var previous = new AllowExplicitHabitStartDate().UpOperations.Should().ContainSingle().Subject
            .Should().BeOfType<SqlOperation>().Subject;
        operation.Sql.Should().Be(previous.Sql);
        operation.SuppressTransaction.Should().BeFalse();
    }
}
