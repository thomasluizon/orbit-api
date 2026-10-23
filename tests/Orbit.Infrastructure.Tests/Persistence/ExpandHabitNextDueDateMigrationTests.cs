using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Migrations;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ExpandHabitNextDueDateMigrationTests
{
    [Fact]
    public void Up_OnlyExpandsStorageAndCopiesEveryCursorBeforeEnablingDualWrites()
    {
        var migration = new ExpandHabitNextDueDate();
        migration.UpOperations.Should().HaveCount(2);
        AssertCursorColumn(migration.UpOperations);
        migration.UpOperations[0].Should().BeOfType<AddColumnOperation>();
        var sql = migration.UpOperations[1].Should().BeOfType<SqlOperation>().Subject;
        sql.SuppressTransaction.Should().BeFalse();
        sql.Sql.Should().StartWith("UPDATE \"Habits\" SET \"NextDueDate\" = \"DueDate\";")
            .And.Contain("NEW.\"DueDate\" := NEW.\"NextDueDate\"")
            .And.Contain("NEW.\"NextDueDate\" := NEW.\"DueDate\"")
            .And.NotContain("StartDate").And.NotContain("UpdatedAtUtc");
        var oldTrigger = new ExpandHabitScheduleAndStartDate().UpOperations.OfType<SqlOperation>()
            .Single(operation => operation.Sql.Contains("CREATE TRIGGER", StringComparison.Ordinal));
        oldTrigger.Sql.Should().Contain("CREATE TRIGGER sync_habit_schedule_expand_on_write");
        StringComparer.Ordinal.Compare("sync_habit_next_due_date_expand_on_write",
            "sync_habit_schedule_expand_on_write").Should().BeNegative();
        RollingMigrationGate.CheckRelease([("next-due-expand", migration.UpOperations)]).Should().BeEmpty();
    }

    [Fact]
    public void Down_CopiesCursorBackAndRemovesOnlyNewCompatibilityObjects()
    {
        var operations = new ExpandHabitNextDueDate().DownOperations;
        operations.Should().HaveCount(2);
        var sql = operations[0].Should().BeOfType<SqlOperation>().Subject.Sql;
        sql.Should().StartWith("UPDATE \"Habits\" SET \"DueDate\" = \"NextDueDate\";")
            .And.Contain("DROP TRIGGER IF EXISTS sync_habit_next_due_date_expand_on_write")
            .And.Contain("DROP FUNCTION IF EXISTS sync_habit_next_due_date_expand()")
            .And.NotContain("sync_habit_schedule_expand");
        var drop = operations[1].Should().BeOfType<DropColumnOperation>().Subject;
        drop.Table.Should().Be("Habits");
        drop.Name.Should().Be("NextDueDate");
    }

    [Fact]
    public void Model_MapsCursorAndDatabaseGeneratedLegacyShadowWithoutPendingChanges()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var habit = context.Model.FindEntityType(typeof(Habit))!;
        var cursor = habit.FindProperty(nameof(Habit.NextDueDate))!;
        cursor.IsShadowProperty().Should().BeFalse();
        cursor.GetColumnName().Should().Be("NextDueDate");
        var legacy = habit.FindProperty("DueDate")!;
        legacy.IsShadowProperty().Should().BeTrue();
        legacy.ValueGenerated.Should().Be(ValueGenerated.OnAddOrUpdate);
        legacy.GetBeforeSaveBehavior().Should().Be(PropertySaveBehavior.Ignore);
        legacy.GetAfterSaveBehavior().Should().Be(PropertySaveBehavior.Ignore);
        typeof(Habit).GetProperty("DueDate").Should().BeNull();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    internal static void AssertCursorColumn(IReadOnlyList<MigrationOperation> operations)
    {
        var column = operations.OfType<AddColumnOperation>().Should()
            .ContainSingle(operation => operation.Table == "Habits" && operation.Name == "NextDueDate").Subject;
        column.ClrType.Should().Be(typeof(DateOnly));
        column.ColumnType.Should().Be("date");
        column.IsNullable.Should().BeFalse();
        column.DefaultValueSql.Should().Be("'0001-01-01'");
    }
}
