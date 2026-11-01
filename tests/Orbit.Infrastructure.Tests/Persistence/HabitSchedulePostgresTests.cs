using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public class HabitSchedulePostgresTests
{
    private static readonly DateOnly Start = new(2026, 4, 3);
    private const string BeforeDrop = "20261025033041_UnmapLegacyHabitScheduleColumns";

    [PostgresFact]
    public Task LegacyFlagOnlyWrite_CannotChangeCanonicalSchedule() =>
        PostgresTestDatabase.WithDatabase(async context =>
        {
            var user = User.Create("Alex", "legacy-schedule@example.test").Value;
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id,
                "Read", FrequencyUnit.Week, 3, Start)).Value;
            await InsertUserBeforeDropAsync(context, user);
            await InsertHabitBeforeDropAsync(context, habit);

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Habits" SET "IsGeneral" = true, "IsFlexible" = false WHERE "Id" = {habit.Id}
                """);

            var stored = await context.Habits.AsNoTracking()
                .Select(row => new { row.Schedule, row.NextDueDate, row.StartDate }).SingleAsync();
            stored.Schedule.Should().Be(HabitSchedule.Flexible);
            stored.NextDueDate.Should().Be(Start);
            stored.StartDate.Should().Be(Start);
            await AssertLegacyColumnsAsync(context);
        }, BeforeDrop);

    [PostgresFact]
    public Task CanonicalWrites_KeepLegacyColumnsDerivedForEverySchedule() =>
        PostgresTestDatabase.WithDatabase(async context =>
        {
            var user = User.Create("Alex", "canonical-schedule@example.test").Value;
            await InsertUserBeforeDropAsync(context, user);
            foreach (var initial in Enum.GetValues<HabitSchedule>())
            {
                var recurring = initial is HabitSchedule.Recurring or HabitSchedule.Flexible;
                var habit = Habit.Create(new HabitCreateParams(initial, user.Id, "Read",
                    recurring ? FrequencyUnit.Day : null, recurring ? 1 : null, Start)).Value;
                await InsertHabitBeforeDropAsync(context, habit);
                (await context.Habits.AsNoTracking().Where(row => row.Id == habit.Id)
                    .Select(row => row.Schedule).SingleAsync()).Should().Be(initial);
                await AssertLegacyColumnsAsync(context);

                foreach (var target in Enum.GetValues<HabitSchedule>())
                {
                    recurring = target is HabitSchedule.Recurring or HabitSchedule.Flexible;
                    habit.Update(new HabitUpdateParams(target, "Read", null,
                        recurring ? FrequencyUnit.Day : null, recurring ? 1 : null, null, false,
                        Start.AddDays(7), UserToday: Start)).IsSuccess.Should().BeTrue();
                    await UpdateHabitBeforeDropAsync(context, habit);
                    var stored = await context.Habits.AsNoTracking().Where(row => row.Id == habit.Id)
                        .Select(row => new { row.Schedule, row.NextDueDate, row.StartDate }).SingleAsync();
                    stored.Schedule.Should().Be(target);
                    stored.NextDueDate.Should().Be(habit.NextDueDate);
                    stored.StartDate.Should().Be(Start);
                    await AssertLegacyColumnsAsync(context);
                }
            }
        }, BeforeDrop);

    [PostgresFact]
    public Task DropLegacyColumns_PreservesAllSchedulesAndRestoresCompatibilityOnRollback() =>
        PostgresTestDatabase.WithDatabase(async context =>
        {
            var user = User.Create("Alex", "drop-schedule@example.test").Value;
            await InsertUserBeforeDropAsync(context, user);
            var habits = Enum.GetValues<HabitSchedule>().Select(schedule =>
            {
                var recurring = schedule is HabitSchedule.Recurring or HabitSchedule.Flexible;
                var habit = Habit.Create(new HabitCreateParams(schedule, user.Id, "Read",
                    recurring ? FrequencyUnit.Day : null, recurring ? 1 : null, Start)).Value;
                habit.PostponeTo(Start.AddDays(7));
                return habit;
            }).ToArray();
            foreach (var habit in habits)
                await InsertHabitBeforeDropAsync(context, habit);
            habits[1].Log(Start.AddDays(7)).IsSuccess.Should().BeTrue();
            var log = habits[1].Logs.Single();
            await UpdateHabitBeforeDropAsync(context, habits[1]);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "HabitLogs" ("Id", "HabitId", "Date", "Value", "CompletionOrdinal",
                    "IsSlip", "Note", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES ({log.Id}, {log.HabitId}, {log.Date}, {log.Value}, {log.CompletionOrdinal},
                    {log.IsSlip}, {log.Note}, {log.CreatedAtUtc}, {log.UpdatedAtUtc})
                """);
            var before = await ReadHabitsAsync(context);
            var scheduleFunction = await ScalarAsync(context,
                "SELECT pg_get_functiondef('sync_habit_schedule_expand()'::regprocedure)");
            var cursorFunction = await ScalarAsync(context,
                "SELECT pg_get_functiondef('sync_habit_next_due_date_expand()'::regprocedure)");
            var triggers = await ScalarAsync(context, TriggerDefinitionsSql);
            var indexes = await ScalarAsync(context, IndexDefinitionsSql);

            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync();
            (await ReadHabitsAsync(context)).Should().Equal(before);
            (await context.HabitLogs.AsNoTracking().SingleAsync()).Id.Should().Be(log.Id);
            await AssertCompatibilityObjectsAbsentAsync(context);

            foreach (var schedule in Enum.GetValues<HabitSchedule>())
            {
                var recurring = schedule is HabitSchedule.Recurring or HabitSchedule.Flexible;
                var habit = Habit.Create(new HabitCreateParams(schedule, user.Id, "Inserted after drop",
                    recurring ? FrequencyUnit.Day : null, recurring ? 1 : null, Start.AddDays(-2))).Value;
                habit.PostponeTo(Start.AddDays(11));
                context.Habits.Add(habit);
                await context.SaveChangesAsync();
                var stored = await context.Habits.AsNoTracking().SingleAsync(row => row.Id == habit.Id);
                stored.NextDueDate.Should().Be(Start.AddDays(11));
                stored.StartDate.Should().Be(Start.AddDays(-2));
                habit.Update(new HabitUpdateParams(schedule, "Updated after drop", null,
                    recurring ? FrequencyUnit.Day : null, recurring ? 1 : null, null, false,
                    Start.AddDays(15), UserToday: Start))
                    .IsSuccess.Should().BeTrue();
                await context.SaveChangesAsync();
                stored = await context.Habits.AsNoTracking().SingleAsync(row => row.Id == habit.Id);
                stored.Schedule.Should().Be(schedule);
                stored.NextDueDate.Should().Be(Start.AddDays(15));
                stored.StartDate.Should().Be(Start.AddDays(-2));
                if (recurring)
                {
                    habit.Update(new HabitUpdateParams(schedule, "Edited start after drop", null,
                        FrequencyUnit.Day, 1, null, false, null,
                        StartDate: Start.AddDays(-3), UserToday: Start)).IsSuccess.Should().BeTrue();
                    await context.SaveChangesAsync();
                    stored = await context.Habits.AsNoTracking().SingleAsync(row => row.Id == habit.Id);
                    stored.NextDueDate.Should().Be(habit.NextDueDate);
                    stored.StartDate.Should().Be(Start.AddDays(-3));
                }
                habit.PostponeTo(Start.AddDays(18));
                await context.SaveChangesAsync();
                stored = await context.Habits.AsNoTracking().SingleAsync(row => row.Id == habit.Id);
                stored.NextDueDate.Should().Be(Start.AddDays(18));
                stored.StartDate.Should().Be(Start.AddDays(recurring ? -3 : -2));
            }

            var beforeRollback = await ReadHabitsAsync(context);
            await migrator.MigrateAsync(BeforeDrop);
            (await ReadHabitsAsync(context)).Should().Equal(beforeRollback);
            await AssertLegacyColumnsAsync(context);
            (await ScalarAsync(context,
                "SELECT pg_get_functiondef('sync_habit_schedule_expand()'::regprocedure)"))
                .Should().Be(scheduleFunction);
            (await ScalarAsync(context,
                "SELECT pg_get_functiondef('sync_habit_next_due_date_expand()'::regprocedure)"))
                .Should().Be(cursorFunction);
            (await ScalarAsync(context, TriggerDefinitionsSql)).Should().Be(triggers);
            (await ScalarAsync(context, IndexDefinitionsSql)).Should().Be(indexes);

            var restored = habits[1];
            restored.PostponeTo(Start.AddDays(20));
            await UpdateHabitBeforeDropAsync(context, restored);
            await AssertLegacyColumnsAsync(context);
            (await context.Habits.AsNoTracking().Where(row => row.Id == restored.Id)
                .Select(row => row.NextDueDate).SingleAsync()).Should().Be(Start.AddDays(20));
            await migrator.MigrateAsync();
            await AssertCompatibilityObjectsAbsentAsync(context);
            (await context.HabitLogs.AsNoTracking().SingleAsync()).Id.Should().Be(log.Id);
        }, BeforeDrop);

    private static Task<int> InsertUserBeforeDropAsync(OrbitDbContext context, User user) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "Name", "Email", "CreatedAtUtc")
            VALUES ({user.Id}, {user.Name}, {user.Email}, {user.CreatedAtUtc})
            """);

    private static Task<int> InsertHabitBeforeDropAsync(OrbitDbContext context, Habit habit) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Habits" ("Id", "UserId", "Title", "Schedule", "FrequencyUnit", "FrequencyQuantity",
                "NextDueDate", "StartDate", "ScheduledStartDate", "IsBadHabit", "IsCompleted", "Days",
                "CreatedAtUtc", "UpdatedAtUtc")
            VALUES ({habit.Id}, {habit.UserId}, {habit.Title}, {(int)habit.Schedule},
                {(int?)habit.FrequencyUnit}, {habit.FrequencyQuantity}, {habit.NextDueDate}, {habit.StartDate},
                {habit.ScheduledStartDate}, {habit.IsBadHabit}, {habit.IsCompleted}, {Array.Empty<string>()},
                {habit.CreatedAtUtc}, {habit.UpdatedAtUtc})
            """);

    private static Task<int> UpdateHabitBeforeDropAsync(OrbitDbContext context, Habit habit) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Habits" SET "Schedule" = {(int)habit.Schedule},
                "FrequencyUnit" = {(int?)habit.FrequencyUnit}, "FrequencyQuantity" = {habit.FrequencyQuantity},
                "NextDueDate" = {habit.NextDueDate}, "StartDate" = {habit.StartDate},
                "ScheduledStartDate" = {habit.ScheduledStartDate}, "IsCompleted" = {habit.IsCompleted},
                "UpdatedAtUtc" = {habit.UpdatedAtUtc}
            WHERE "Id" = {habit.Id}
            """);

    private static Task<List<(Guid Id, HabitSchedule Schedule, DateOnly NextDueDate, DateOnly StartDate)>>
        ReadHabitsAsync(OrbitDbContext context) => context.Habits.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(habit => habit.Id)
            .Select(habit => new ValueTuple<Guid, HabitSchedule, DateOnly, DateOnly>(
                habit.Id, habit.Schedule, habit.NextDueDate, habit.StartDate)).ToListAsync();

    private const string TriggerDefinitionsSql = """
        SELECT string_agg(pg_get_triggerdef(oid), E'\n' ORDER BY tgname)
        FROM pg_trigger WHERE tgrelid = '"Habits"'::regclass AND NOT tgisinternal
        """;

    private const string IndexDefinitionsSql = """
        SELECT string_agg(indexdef, E'\n' ORDER BY indexname) FROM pg_indexes
        WHERE tablename = 'Habits' AND indexname IN ('IX_Habits_Reminder', 'IX_Habits_DueDateAdvancement')
        """;

    private static async Task AssertCompatibilityObjectsAbsentAsync(OrbitDbContext context)
    {
        Convert.ToInt64(await ScalarAsync(context, """
            SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public'
                AND table_name = 'Habits' AND column_name IN ('IsGeneral', 'IsFlexible', 'DueDate')
            """)).Should().Be(0);
        (await ScalarAsync(context, TriggerDefinitionsSql)).Should().Be(DBNull.Value);
        (await ScalarAsync(context, IndexDefinitionsSql)).Should().Be(DBNull.Value);
        Convert.ToBoolean(await ScalarAsync(context, """
            SELECT to_regprocedure('sync_habit_schedule_expand()') IS NULL
                AND to_regprocedure('sync_habit_next_due_date_expand()') IS NULL
            """)).Should().BeTrue();
    }

    private static async Task<object?> ScalarAsync(OrbitDbContext context, string sql)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

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
