using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ReminderScheduleProbeTests
{
    [PostgresFact]
    public Task PendingMigrations_FromProductionBaseline_ApplyTogetherWithValidConcurrentIndexes() =>
        PostgresTestDatabase.WithDatabase(async db =>
        {
            var existing = ProcessedPlayNotification.Create("completed-before-upgrade");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ProcessedPlayNotifications" ("Id", "MessageId", "ProcessedAtUtc")
                VALUES ({existing.Id}, {existing.MessageId}, {existing.ProcessedAtUtc})
                """);
            await db.GetService<IMigrator>().MigrateAsync();
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await db.ProcessedPlayNotifications.AsNoTracking().SingleAsync()).ProcessingLeaseId.Should().BeNull();
            await db.Database.OpenConnectionAsync();
            try
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                    WHERE c.relname IN ('IX_Habits_Reminder', 'IX_SentReminders_HabitId_Date')
                      AND i.indisvalid AND i.indisready
                    """;
                Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(2);
            }
            finally { await db.Database.CloseConnectionAsync(); }
            var user = User.Create("User", "postgres-probe@example.com").Value;
            db.Users.Add(user);
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Habit", FrequencyUnit.Day, 1,
                new DateOnly(2027, 9, 26), ReminderEnabled: true, DueTime: new TimeOnly(9, 0), ReminderTimes: [0])).Value;
            db.Habits.Add(habit);
            await db.SaveChangesAsync();
            var version = (await db.Set<ReminderScheduleProbe>().AsNoTracking().SingleAsync()).Version;
            await db.Habits.Where(row => row.Id == habit.Id).ExecuteUpdateAsync(s => s.SetProperty(row => row.Title, "Changed"));
            (await db.Set<ReminderScheduleProbe>().AsNoTracking().SingleAsync()).Version.Should().BeGreaterThan(version);
        }, "20261102072521_SeedPublicEndpointAbuseQuotas");

    [Fact]
    public async Task BulkMutation_ChangesOnlyOwnerRevision_AndRollsBackWithRows()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var a = User.Create("A", "probe-a@example.com").Value;
        var b = User.Create("B", "probe-b@example.com").Value;
        db.Users.AddRange(a, b);
        var habits = new[] { a, b }.Select(user => Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            user.Id, user.Name, FrequencyUnit.Day, 1, new DateOnly(2027, 9, 26),
            DueTime: new TimeOnly(9, 0), ReminderEnabled: true, ReminderTimes: [0])).Value).ToArray();
        db.Habits.AddRange(habits);
        await db.SaveChangesAsync();
        var before = await db.Set<ReminderScheduleProbe>().AsNoTracking().ToDictionaryAsync(p => p.UserId, p => p.Version);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Habits.Where(h => h.Id == habits[0].Id).ExecuteUpdateAsync(s => s.SetProperty(h => h.Title, "Edited"));
            var changed = await db.Set<ReminderScheduleProbe>().AsNoTracking().ToDictionaryAsync(p => p.UserId, p => p.Version);
            changed[a.Id].Should().BeGreaterThan(before[a.Id]);
            changed[b.Id].Should().Be(before[b.Id]);
        }
        var after = await db.Set<ReminderScheduleProbe>().AsNoTracking().ToDictionaryAsync(p => p.UserId, p => p.Version);
        after.Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task AccountCascade_DoesNotRecreateAnOrphanProbe()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("User", "probe-delete@example.com").Value;
        db.Users.Add(user);
        db.Habits.Add(Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Habit", FrequencyUnit.Day, 1,
            new DateOnly(2027, 9, 26), ReminderEnabled: true, DueTime: new TimeOnly(9, 0), ReminderTimes: [0])).Value);
        await db.SaveChangesAsync();
        await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        (await db.Set<ReminderScheduleProbe>().CountAsync()).Should().Be(0);
    }
}
