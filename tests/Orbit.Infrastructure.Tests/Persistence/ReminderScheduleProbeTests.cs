using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class ReminderScheduleProbeTests
{
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
        var before = await db.ReminderScheduleProbes.AsNoTracking().ToDictionaryAsync(p => p.UserId, p => p.Version);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Habits.Where(h => h.Id == habits[0].Id).ExecuteUpdateAsync(s => s.SetProperty(h => h.Title, "Edited"));
            var changed = await db.ReminderScheduleProbes.AsNoTracking().ToDictionaryAsync(p => p.UserId, p => p.Version);
            changed[a.Id].Should().BeGreaterThan(before[a.Id]);
            changed[b.Id].Should().Be(before[b.Id]);
        }
        var after = await db.ReminderScheduleProbes.AsNoTracking().ToDictionaryAsync(p => p.UserId, p => p.Version);
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
        (await db.ReminderScheduleProbes.CountAsync()).Should().Be(0);
    }
}
