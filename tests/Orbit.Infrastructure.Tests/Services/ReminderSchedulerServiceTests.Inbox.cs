using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Notifications;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class ReminderSchedulerServiceTests
{
    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public async Task Inbox_MidnightDaylightSavingGapDoesNotPreventRecording(int minutes)
    {
        var db = CreateSqliteDbContext();
        var (habit, _) = await SeedRepeat();
        var midnight = new DateTime(1, 1, 1, 0, 0, 0);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2027, 1, 1), new(2027, 12, 31),
            TimeSpan.FromHours(1), TimeZoneInfo.TransitionTime.CreateFixedDateRule(midnight, 3, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(midnight, 11, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Midnight change", TimeSpan.Zero, "Midnight change", "Standard", "Daylight", [rule]);
        var sentAtUtc = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);
        var reminder = Notification.Create(habit.UserId, habit.Title, "Latest", "/", habit.Id);

        await HabitReminderInbox.RefreshAsync(new GenericRepository<Notification>(db), reminder, zone, sentAtUtc, default);
        await db.SaveChangesAsync();

        (await db.Notifications.SingleAsync()).CreatedAtUtc.Should().Be(sentAtUtc);
    }

    [Fact]
    public async Task Repeat_InboxStartsANewRowAtLocalMidnight()
    {
        var db = CreateSqliteDbContext();
        var firstAtUtc = UtcToday.AddDays(1).ToDateTime(new(2, 30), DateTimeKind.Utc);
        var (habit, _) = await SeedRepeat(new(30, StartAtUtc: firstAtUtc.AddMinutes(-30)));
        var user = await db.Users.SingleAsync();
        user.SetTimeZone("America/Sao_Paulo");
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(firstAtUtc));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);

        await service.CheckAndSendReminders(default);
        clock.SetUtcNow(new(firstAtUtc.AddMinutes(30)));
        await service.CheckAndSendReminders(default);

        var rows = await db.Notifications.OrderBy(n => n.CreatedAtUtc).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].DedupeKey.Should().Be(HabitReminderInbox.Key(habit.Id, UtcToday));
        rows[1].DedupeKey.Should().Be(HabitReminderInbox.Key(habit.Id, UtcToday.AddDays(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeat_NextSendCollapsesLegacyDuplicatesAndRefreshesReadRow(bool keyed)
    {
        var db = CreateSqliteDbContext();
        var (habit, startAtUtc) = await SeedRepeat();
        var duplicates = Enumerable.Range(1, 3).Select(i => Notification.Create(habit.UserId, habit.Title,
            "Old", "/", habit.Id, keyed && i == 3 ? HabitReminderInbox.Key(habit.Id, UtcToday) : null)).ToList();
        db.Notifications.AddRange(duplicates);
        foreach (var row in duplicates)
            db.Entry(row).Property(n => n.CreatedAtUtc).CurrentValue = startAtUtc;
        duplicates[2].MarkAsRead();
        var previousDay = Notification.Create(habit.UserId, habit.Title, "History", "/", habit.Id);
        var otherHabit = Notification.Create(habit.UserId, "Other", "Other", "/", Guid.NewGuid());
        db.Notifications.AddRange(previousDay, otherHabit);
        db.Entry(previousDay).Property(n => n.CreatedAtUtc).CurrentValue = startAtUtc.AddDays(-1);
        await db.SaveChangesAsync();

        var sentAtUtc = startAtUtc.AddHours(8);
        await CreateService(db, Substitute.For<IPushNotificationService>(), new MutableTimeProvider(new(sentAtUtc)))
            .CheckAndSendReminders(default);

        db.ChangeTracker.Clear();
        var current = (await db.Notifications.Where(n => n.HabitId == habit.Id && n.Id != previousDay.Id).ToListAsync())
            .Should().ContainSingle().Subject;
        current.IsRead.Should().BeFalse();
        current.CreatedAtUtc.Should().Be(sentAtUtc);
        (await db.Notifications.IgnoreQueryFilters().CountAsync(n => n.IsDeleted)).Should().Be(2);
        (await db.Notifications.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Scheduled_MultipleSendsShareOneDailyInboxRow()
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Water", FrequencyUnit.Day, 1,
            UtcToday, ReminderEnabled: true, ScheduledReminders:
            [new(ScheduledReminderWhen.SameDay, new(8, 0)), new(ScheduledReminderWhen.SameDay, new(9, 0))])).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(UtcToday.ToDateTime(new(8, 0), DateTimeKind.Utc)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        clock.SetUtcNow(new(UtcToday.ToDateTime(new(9, 0), DateTimeKind.Utc)));
        await service.CheckAndSendReminders(default);

        (await db.SentReminders.CountAsync()).Should().Be(2);
        var row = (await db.Notifications.ToListAsync()).Should().ContainSingle().Subject;
        row.CreatedAtUtc.Should().Be(clock.GetUtcNow().UtcDateTime);
    }
}
