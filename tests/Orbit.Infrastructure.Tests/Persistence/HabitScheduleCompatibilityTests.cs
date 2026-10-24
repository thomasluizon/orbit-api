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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_AllScheduleTransitionsKeepLegacyColumnsCompatible(bool asyncSave)
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
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
    }

    [Fact]
    public void CompatibilityFlags_ExistOnlyInPersistence()
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
            model.FindProperty(name)!.IsShadowProperty().Should().BeTrue();
        }
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private static async Task AssertStoredAsync(SqliteOrbitDbContextFactory factory, Guid id, HabitSchedule schedule)
    {
        await using var readback = factory.CreateContext();
        var row = await readback.Habits.Where(habit => habit.Id == id).Select(habit => new
        {
            habit.Schedule,
            IsGeneral = EF.Property<bool>(habit, "IsGeneral"),
            IsFlexible = EF.Property<bool>(habit, "IsFlexible"),
            habit.NextDueDate,
            habit.StartDate
        }).SingleAsync();
        row.Schedule.Should().Be(schedule);
        row.IsGeneral.Should().Be(schedule == HabitSchedule.General);
        row.IsFlexible.Should().Be(schedule == HabitSchedule.Flexible);
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
