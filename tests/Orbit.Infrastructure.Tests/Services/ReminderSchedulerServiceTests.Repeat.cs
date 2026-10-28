using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Tests.Services;

public partial class ReminderSchedulerServiceTests
{
    [Theory]
    [InlineData(HabitSchedule.General)]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.Recurring)]
    public async Task Repeat_CrossesMidnightAndDeduplicates(HabitSchedule schedule)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        var start = UtcToday.ToDateTime(new(20, 0), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(schedule, user.Id, "Drink water",
            schedule == HabitSchedule.General ? null : FrequencyUnit.Day,
            schedule == HabitSchedule.General ? null : 1, UtcToday,
            ReminderEnabled: true, RepeatReminder: new(480, StartAtUtc: start))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(start.AddHours(8)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        await service.CheckAndSendReminders(default);
        var sent = await db.SentReminders.ToListAsync();
        sent.Should().ContainSingle();
        sent[0].ReminderAtUtc.Should().Be(start.AddHours(8));
    }

    [Fact]
    public async Task Repeat_IgnoredReminderGetsOneFollowUpThenContinuesOriginalClock()
    {
        var db = CreateSqliteDbContext();
        var (habit, start) = await SeedRepeat();
        var clock = new MutableTimeProvider(new(start.AddHours(8)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        clock.SetUtcNow(new(start.AddHours(8).AddMinutes(15)));
        await service.CheckAndSendReminders(default);
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(2);
        clock.SetUtcNow(new(start.AddHours(16)));
        await service.CheckAndSendReminders(default);
        var sent = await db.SentReminders.OrderBy(r => r.ReminderAtUtc).ToListAsync();
        sent.Should().HaveCount(3);
        sent[1].ParentReminderId.Should().Be(sent[0].Id);
        sent[2].ReminderAtUtc.Should().Be(start.AddHours(16));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeat_LateOrCorrectedDoseMovesNextReminder(bool corrected)
    {
        var db = CreateSqliteDbContext();
        var (habit, start) = await SeedRepeat();
        var dose = start.AddHours(8).AddMinutes(30);
        var log = HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, UtcToday, 1, 0, dose);
        log.CorrectTakenAtUtc(corrected ? dose.AddMinutes(-30) : dose, dose);
        db.HabitLogs.Add(log);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(dose.AddHours(8).AddMinutes(-1)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        if (corrected)
        {
            habit.LoadScheduleLogsForRead([log]);
            habit.CorrectDose(log.Id, dose, dose).IsSuccess.Should().BeTrue();
            await db.SaveChangesAsync();
        }
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(0);
        clock.SetUtcNow(new(dose.AddHours(8)));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(dose.AddHours(8));
    }

    [Fact]
    public async Task Repeat_BackfilledLogDoesNotMoveClock()
    {
        var db = CreateSqliteDbContext();
        var (habit, start) = await SeedRepeat();
        db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, UtcToday.AddDays(-1), 1, 0, start.AddHours(2)));
        await db.SaveChangesAsync();
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), new MutableTimeProvider(new(start.AddHours(8))));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(start.AddHours(8));
    }

    [Fact]
    public async Task Repeat_ActiveWindowCanCrossMidnight()
    {
        var db = CreateSqliteDbContext();
        var (habit, start) = await SeedRepeat(new(120, new(22, 0), new(6, 0), UtcToday.ToDateTime(new(5, 0), DateTimeKind.Utc)));
        var clock = new MutableTimeProvider(new(UtcToday.ToDateTime(new(7, 0), DateTimeKind.Utc)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(0);
        clock.SetUtcNow(new(UtcToday.ToDateTime(new(22, 0), DateTimeKind.Utc)));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(clock.GetUtcNow().UtcDateTime);
    }

    [Theory]
    [InlineData(HabitSchedule.Recurring, 0)]
    [InlineData(HabitSchedule.Flexible, 1)]
    [InlineData(HabitSchedule.General, 1)]
    public async Task Repeat_OnlyRecurringStopsAfterDailyCompletion(HabitSchedule schedule, int expected)
    {
        var db = CreateSqliteDbContext();
        var (habit, start) = await SeedRepeat(schedule: schedule);
        var log = HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, UtcToday, 1, 0, start);
        db.HabitLogs.Add(log);
        await db.SaveChangesAsync();
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), new MutableTimeProvider(new(start.AddHours(8))));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(expected);
    }

    [Fact]
    public async Task Repeat_UserTimeZoneAndDstPreserveElapsedInterval()
    {
        var db = CreateSqliteDbContext();
        var start = new DateTime(2027, 3, 14, 5, 0, 0, DateTimeKind.Utc);
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetTimeZone("America/New_York");
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id, "Drink water", FrequencyUnit.Day, 1,
            new(2027, 3, 14), ReminderEnabled: true, RepeatReminder: new(480, StartAtUtc: start))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), new MutableTimeProvider(new(start.AddHours(8))));
        await service.CheckAndSendReminders(default);
        var sent = await db.SentReminders.SingleAsync();
        sent.ReminderAtUtc.Should().Be(start.AddHours(8));
        sent.Date.Should().Be(new DateOnly(2027, 3, 14));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("same-day")]
    [InlineData("day-before")]
    [InlineData("repeat")]
    public async Task EndDate_AllReminderKindsStopAfterLocalEndDate(string kind)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Drink water", FrequencyUnit.Day, 1,
            UtcToday, ReminderEnabled: true, EndDate: UtcToday,
            DueTime: kind == "relative" ? new(23, 30) : null,
            RelativeReminders: kind == "relative" ? [new(-60)] : null,
            ScheduledReminders: kind is "same-day" or "day-before"
                ? [new(kind == "same-day" ? ScheduledReminderWhen.SameDay : ScheduledReminderWhen.DayBefore, new(0, 0))] : null,
            RepeatReminder: kind == "repeat" ? new(480, StartAtUtc: UtcToday.ToDateTime(new(20, 0), DateTimeKind.Utc)) : null)).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(UtcToday.AddDays(1).ToDateTime(new(4, 0), DateTimeKind.Utc)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(0);
    }

    private async Task<(Habit Habit, DateTime Start)> SeedRepeat(RepeatReminderOptions? options = null,
        HabitSchedule schedule = HabitSchedule.Flexible)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        var start = UtcToday.ToDateTime(new(4, 0), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(schedule, user.Id, "Drink water",
            schedule == HabitSchedule.General ? null : FrequencyUnit.Day,
            schedule == HabitSchedule.General ? null : 1, UtcToday,
            ReminderEnabled: true, RepeatReminder: options ?? new(480, StartAtUtc: start))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        return (habit, start);
    }
}
