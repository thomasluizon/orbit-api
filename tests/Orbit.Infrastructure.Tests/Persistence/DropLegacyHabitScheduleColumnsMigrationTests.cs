using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class DropLegacyHabitScheduleColumnsMigrationTests
{
    [Fact]
    public void Up_DropsBothCompatibilityTriggersAndDependentIndexesBeforeColumns()
    {
        var operations = new DropLegacyHabitScheduleColumns().UpOperations;
        var cleanup = operations.First().Should().BeOfType<SqlOperation>().Subject;
        cleanup.SuppressTransaction.Should().BeFalse();
        cleanup.Sql.Should().Contain("DROP TRIGGER IF EXISTS sync_habit_schedule_expand_on_write ON \"Habits\"")
            .And.Contain("DROP FUNCTION IF EXISTS sync_habit_schedule_expand()")
            .And.Contain("DROP TRIGGER IF EXISTS sync_habit_next_due_date_expand_on_write ON \"Habits\"")
            .And.Contain("DROP FUNCTION IF EXISTS sync_habit_next_due_date_expand()")
            .And.Contain("DROP INDEX IF EXISTS \"IX_Habits_Reminder\"")
            .And.Contain("DROP INDEX IF EXISTS \"IX_Habits_DueDateAdvancement\"")
            .And.NotContain("CASCADE").And.NotContain("UPDATE \"Habits\"");
        operations.Skip(1).Should().AllBeOfType<DropColumnOperation>();
        operations.OfType<DropColumnOperation>().Select(column => (column.Table, column.Name))
            .Should().BeEquivalentTo(new[] { ("Habits", "IsGeneral"), ("Habits", "IsFlexible"), ("Habits", "DueDate") });
        RollingMigrationGate.Check("drop-legacy-schedule", operations).Should().ContainSingle();
    }

    [Fact]
    public void Down_RestoresExactFunctionsTriggersAndIndexesAfterBackfill()
    {
        var operations = new DropLegacyHabitScheduleColumns().DownOperations;
        operations.OfType<AddColumnOperation>().Select(column => (column.Table, column.Name))
            .Should().BeEquivalentTo(new[] { ("Habits", "IsGeneral"), ("Habits", "IsFlexible"), ("Habits", "DueDate") });
        var sql = operations.OfType<SqlOperation>().ToArray();
        sql[0].Sql.Should().Contain("\"IsGeneral\" = \"Schedule\" = 3")
            .And.Contain("\"IsFlexible\" = \"Schedule\" = 2")
            .And.Contain("\"DueDate\" = \"NextDueDate\"");
        sql[1].Sql.Should().Be(new UnmapLegacyHabitScheduleColumns().UpOperations
            .OfType<SqlOperation>().Single().Sql);
        var scheduleTrigger = new ExpandHabitScheduleAndStartDate().UpOperations.OfType<SqlOperation>()
            .Single(operation => operation.Sql.Contains("CREATE TRIGGER", StringComparison.Ordinal)).Sql;
        var cursorTrigger = new ExpandHabitNextDueDate().UpOperations.OfType<SqlOperation>().Single().Sql;
        sql[2].Sql.Trim().Should().Be(Extract(scheduleTrigger,
            "CREATE TRIGGER sync_habit_schedule_expand_on_write", "();"));
        sql[3].Sql.Trim().Should().Be(Extract(cursorTrigger,
            "CREATE FUNCTION sync_habit_next_due_date_expand()", "();"));
        sql.Skip(4).Select(operation => operation.Sql).Should().Equal(new AdoptPerformanceIndexes()
            .UpOperations.OfType<SqlOperation>().Take(2).Select(operation => operation.Sql));
        operations.OfType<AlterColumnOperation>().Should().ContainSingle(column =>
            column.Name == "DueDate" && !column.IsNullable && column.DefaultValue == null && column.DefaultValueSql == null);
        sql.Should().OnlyContain(operation => !operation.SuppressTransaction);
    }

    private static string Extract(string sql, string start, string end)
    {
        var first = sql.IndexOf(start, StringComparison.Ordinal);
        var last = sql.IndexOf(end, first, StringComparison.Ordinal) + end.Length;
        return sql[first..last].Trim();
    }
}
