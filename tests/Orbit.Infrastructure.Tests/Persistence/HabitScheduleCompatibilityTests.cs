using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitScheduleCompatibilityTests
{
    private static readonly DateOnly Start = new(2026, 4, 3);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Save_AllScheduleTransitionsIgnoreLegacyColumns(bool asyncSave, bool retainLegacyColumns)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        if (retainLegacyColumns)
        {
            await context.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "Habits" ADD COLUMN "IsGeneral" INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE "Habits" ADD COLUMN "IsFlexible" INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE "Habits" ADD COLUMN "DueDate" TEXT NOT NULL DEFAULT '1999-01-01';
                """);
        }
        var user = User.Create("Alex", "schedule@example.test").Value;
        context.Users.Add(user);
        foreach (var initial in Enum.GetValues<HabitSchedule>())
        {
            var recurring = initial is HabitSchedule.Recurring or HabitSchedule.Flexible;
            var habit = Habit.Create(new HabitCreateParams(initial, user.Id, "Read",
                recurring ? FrequencyUnit.Day : null, recurring ? 1 : null, Start)).Value;
            context.Habits.Add(habit);
            await SaveAsync(context, asyncSave);
            await AssertStoredAsync(factory, habit.Id, initial);
            foreach (var target in Enum.GetValues<HabitSchedule>())
            {
                recurring = target is HabitSchedule.Recurring or HabitSchedule.Flexible;
                habit.Update(new HabitUpdateParams(target, "Read", null,
                    recurring ? FrequencyUnit.Day : null, recurring ? 1 : null, null, false, null))
                    .IsSuccess.Should().BeTrue();
                await SaveAsync(context, asyncSave);
                await AssertStoredAsync(factory, habit.Id, target);
            }
        }
        if (retainLegacyColumns)
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM "Habits"
                WHERE "IsGeneral" = 1 AND "IsFlexible" = 1 AND "DueDate" = '1999-01-01'
                """;
            Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(4);
        }
    }

    [Fact]
    public void Model_DoesNotMapLegacyColumnsOrHavePendingChanges()
    {
        using var context = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=unused").Options);
        var model = context.Model.FindEntityType(typeof(Habit))!;
        foreach (var name in new[] { "IsGeneral", "IsFlexible" })
        {
            typeof(Habit).GetProperty(name).Should().BeNull();
            typeof(HabitCreateParams).GetProperty(name).Should().BeNull();
            typeof(HabitUpdateParams).GetProperty(name).Should().BeNull();
            typeof(HabitScheduleSnapshot).GetProperty(name).Should().BeNull();
            model.FindProperty(name).Should().BeNull();
        }
        typeof(Habit).GetProperty("DueDate").Should().BeNull();
        model.FindProperty("DueDate").Should().BeNull();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private static async Task AssertStoredAsync(SqliteOrbitDbContextFactory factory, Guid id, HabitSchedule schedule)
    {
        await using var readback = factory.CreateContext();
        var row = await readback.Habits.SingleAsync(habit => habit.Id == id);
        row.Schedule.Should().Be(schedule);
        row.NextDueDate.Should().Be(Start);
        row.StartDate.Should().Be(Start);
    }

    private static async Task SaveAsync(OrbitDbContext context, bool asyncSave)
    {
        if (asyncSave)
            await context.SaveChangesAsync();
        else
            context.SaveChanges();
    }
}
