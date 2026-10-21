using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class AllowExplicitHabitStartDateMigrationTests
{
    [Fact]
    public void CorrectiveMigration_ReplacesOnlyFunctionAndKeepsLegacySynchronization()
    {
        var migration = new AllowExplicitHabitStartDate();
        var sql = migration.UpOperations.Should().ContainSingle().Subject.Should().BeOfType<SqlOperation>().Subject.Sql;
        sql.Should().StartWith("CREATE OR REPLACE FUNCTION sync_habit_schedule_expand()")
            .And.Contain("RAISE EXCEPTION 'Invalid legacy habit schedule'")
            .And.Contain("NEW.\"Schedule\" := CASE")
            .And.Contain("ELSIF NEW.\"StartDate\" IS NULL OR NEW.\"StartDate\" = DATE '0001-01-01' THEN")
            .And.NotContain("UPDATE \"Habits\"").And.NotContain("HabitLogs");
        var down = migration.DownOperations.Should().ContainSingle().Subject.Should().BeOfType<SqlOperation>().Subject.Sql;
        down.Should().Contain("ELSE\n        NEW.\"StartDate\" := OLD.\"StartDate\";")
            .And.NotContain("UPDATE \"Habits\"").And.NotContain("DROP COLUMN");
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
