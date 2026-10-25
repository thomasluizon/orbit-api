using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitSchedulePostgresTests
{
    private static readonly DateOnly Start = new(2026, 4, 3);

    [PostgresFact]
    public Task LegacyFlagOnlyWrite_CannotChangeCanonicalSchedule() =>
        PostgresTestDatabase.WithDatabase(async context =>
        {
            var user = User.Create("Alex", "legacy-schedule@example.test").Value;
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id,
                "Read", FrequencyUnit.Week, 3, Start)).Value;
            context.Users.Add(user);
            context.Habits.Add(habit);
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Habits" SET "IsGeneral" = true, "IsFlexible" = false WHERE "Id" = {habit.Id}
                """);

            var stored = await context.Habits.AsNoTracking().SingleAsync();
            stored.Schedule.Should().Be(HabitSchedule.Flexible);
            stored.NextDueDate.Should().Be(Start);
            stored.StartDate.Should().Be(Start);
            await AssertLegacyColumnsAsync(context);
        });

    [PostgresFact]
    public Task CanonicalWrites_KeepLegacyColumnsDerivedForEverySchedule() =>
        PostgresTestDatabase.WithDatabase(async context =>
        {
            var user = User.Create("Alex", "canonical-schedule@example.test").Value;
            context.Users.Add(user);
            foreach (var initial in Enum.GetValues<HabitSchedule>())
            {
                var recurring = initial is HabitSchedule.Recurring or HabitSchedule.Flexible;
                var habit = Habit.Create(new HabitCreateParams(initial, user.Id, "Read",
                    recurring ? FrequencyUnit.Day : null, recurring ? 1 : null, Start)).Value;
                context.Habits.Add(habit);
                await context.SaveChangesAsync();
                (await context.Habits.AsNoTracking().SingleAsync(row => row.Id == habit.Id))
                    .Schedule.Should().Be(initial);
                await AssertLegacyColumnsAsync(context);

                foreach (var target in Enum.GetValues<HabitSchedule>())
                {
                    recurring = target is HabitSchedule.Recurring or HabitSchedule.Flexible;
                    habit.Update(new HabitUpdateParams(target, "Read", null,
                        recurring ? FrequencyUnit.Day : null, recurring ? 1 : null, null, false,
                        Start.AddDays(7), UserToday: Start)).IsSuccess.Should().BeTrue();
                    await context.SaveChangesAsync();
                    var stored = await context.Habits.AsNoTracking().SingleAsync(row => row.Id == habit.Id);
                    stored.Schedule.Should().Be(target);
                    stored.NextDueDate.Should().Be(habit.NextDueDate);
                    stored.StartDate.Should().Be(Start);
                    await AssertLegacyColumnsAsync(context);
                }
            }
        });

    private static async Task AssertLegacyColumnsAsync(OrbitDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM "Habits"
                WHERE "IsGeneral" <> ("Schedule" = 3)
                   OR "IsFlexible" <> ("Schedule" = 2)
                   OR "DueDate" <> "NextDueDate"
                """;
            Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(0);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
