using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Migrations;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HealHabitModelDataMigrationTests
{
    [Fact]
    public void Up_RepairsExistingRowsAndGuardsWritesFromOldInstances()
    {
        var sql = GetSql("Up");

        sql.Should().Contain("BEFORE INSERT OR UPDATE ON \"Habits\"");
        sql.Should().Contain("UPDATE \"Habits\" SET \"EndDate\" = \"EndDate\"");
        sql.Should().Contain("NEW.\"ScheduledReminders\" := '[]'::jsonb");
        sql.Should().Contain($"LIMIT {DomainConstants.MaxReminderTimes}");
        sql.Should().Contain("RAISE NOTICE");
        sql.Should().Contain("RAISE EXCEPTION");
    }

    [Fact]
    public void Down_RemovesTheWriteGuard()
    {
        var sql = GetSql("Down");

        sql.Should().Contain("DROP TRIGGER IF EXISTS");
        sql.Should().Contain("DROP FUNCTION IF EXISTS");
    }

    [Fact]
    public void FoldScheduledReminders_SeededMixedRow_PreservesReminderCountWithinCap()
    {
        var scheduled = new[]
        {
            new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0)),
            new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(20, 0))
        };
        var storedJson = JsonSerializer.Serialize(scheduled);
        storedJson.Should().Contain("\"When\":\"same_day\"");
        storedJson.Should().Contain("\"Time\":\"09:00:00\"");
        var storedScheduled = JsonSerializer.Deserialize<List<ScheduledReminderTime>>(storedJson)!;
        var storedOffsets = JsonSerializer.Deserialize<List<int>>("[15]")!;

        var folded = ReminderStoreNormalizer.FoldScheduledReminders(
            new TimeOnly(10, 0), storedOffsets, storedScheduled);

        folded.Should().Equal(15, 60, 840);
        folded.Should().HaveCount(Math.Min(storedOffsets.Count + storedScheduled.Count, DomainConstants.MaxReminderTimes));
    }

    private static string GetSql(string methodName)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(HealHabitModelData).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new HealHabitModelData(), [builder]);
        return builder.Operations.OfType<SqlOperation>().Should().ContainSingle().Subject.Sql;
    }
}
