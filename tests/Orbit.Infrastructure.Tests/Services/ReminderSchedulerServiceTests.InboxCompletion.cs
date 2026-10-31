using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Notifications;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class ReminderSchedulerServiceTests
{
    [Theory]
    [InlineData("log")]
    [InlineData("bulk-log")]
    [InlineData("skip")]
    [InlineData("bulk-skip")]
    public async Task Repeat_SendAndLogPreserveUntypedRows(string action)
    {
        var db = CreateSqliteDbContext();
        var (habit, startAtUtc) = await SeedRepeat(new(30, StartAtUtc: UtcToday.ToDateTime(new(4, 0), DateTimeKind.Utc)));
        var sentAtUtc = startAtUtc.AddMinutes(30);
        var slipAlert = Notification.Create(habit.UserId, habit.Title, "Next at 05:00", NotificationUrls.Home, habit.Id);
        var legacyReminder = Notification.Create(habit.UserId, habit.Title, "Next at 05:00", NotificationUrls.Home, habit.Id);
        db.Notifications.AddRange(slipAlert, legacyReminder);
        foreach (var row in new[] { slipAlert, legacyReminder })
            db.Entry(row).Property(n => n.CreatedAtUtc).CurrentValue = sentAtUtc;
        await db.SaveChangesAsync();

        await CreateService(db, Substitute.For<IPushNotificationService>(), new MutableTimeProvider(new(sentAtUtc)))
            .CheckAndSendReminders(default);

        db.ChangeTracker.Clear();
        var beforeLog = await db.Notifications.ToListAsync();
        beforeLog.Should().HaveCount(3);
        beforeLog.Where(n => n.Id == slipAlert.Id || n.Id == legacyReminder.Id)
            .Should().OnlyContain(n => n.DedupeKey == null && !n.IsRead && n.CreatedAtUtc == sentAtUtc);
        var reminder = beforeLog.Should().ContainSingle(n => n.DedupeKey == HabitReminderInbox.Key(habit.Id, UtcToday)).Subject;

        await ApplyInboxActionAsync(db, habit, UtcToday, TimeZoneInfo.Utc, action);

        db.ChangeTracker.Clear();
        var afterLog = await db.Notifications.ToListAsync();
        afterLog.Should().HaveCount(3);
        afterLog.Single(n => n.Id == reminder.Id).IsRead.Should().BeTrue();
        afterLog.Where(n => n.Id != reminder.Id).Should().OnlyContain(n => !n.IsRead && n.DedupeKey == null
            && n.Title == habit.Title && n.Body == "Next at 05:00" && n.CreatedAtUtc == sentAtUtc);
    }

    [Theory]
    [InlineData("scheduled", "log")]
    [InlineData("scheduled", "bulk-log")]
    [InlineData("scheduled", "skip")]
    [InlineData("scheduled", "bulk-skip")]
    [InlineData("relative-clock", "log")]
    [InlineData("relative-clock", "skip")]
    [InlineData("relative-minutes", "log")]
    [InlineData("relative-minutes", "skip")]
    public async Task DayBefore_InboxIsReadWhenTheDueDayIsLoggedOrSkipped(string kind, string action)
    {
        var db = CreateSqliteDbContext();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetTimeZone(zone.Id);
        var dueDate = UtcToday.AddDays(1);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Water", FrequencyUnit.Day, 1,
            dueDate, ReminderEnabled: true, DueTime: kind == "scheduled" ? null : new(23, 30),
            ReminderTimes: kind == "relative-minutes" ? [1440] : [],
            RelativeReminders: kind == "relative-clock" ? [new(When: ScheduledReminderWhen.DayBefore, Time: new(23, 30))] : null,
            ScheduledReminders: kind == "scheduled" ? [new(ScheduledReminderWhen.DayBefore, new(23, 30))] : null)).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var sentAtUtc = dueDate.ToDateTime(new(2, 30), DateTimeKind.Utc);

        await CreateService(db, Substitute.For<IPushNotificationService>(), new MutableTimeProvider(new(sentAtUtc)))
            .CheckAndSendReminders(default);

        var reminder = await db.Notifications.SingleAsync();
        reminder.IsRead.Should().BeFalse();
        reminder.CreatedAtUtc.Should().Be(sentAtUtc);
        reminder.DedupeKey.Should().Be(HabitReminderInbox.Key(habit.Id, UtcToday));
        reminder.ReminderDate.Should().Be(dueDate);
        await ApplyInboxActionAsync(db, habit, dueDate, zone, action);

        db.ChangeTracker.Clear();
        (await db.Notifications.SingleAsync()).IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task Scheduled_DifferentOccurrencesShareOneSendDayRowAndOnlyTheLatestOccurrenceClearsIt()
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Water", FrequencyUnit.Day, 1,
            UtcToday, ReminderEnabled: true, ScheduledReminders:
            [new(ScheduledReminderWhen.SameDay, new(8, 0)), new(ScheduledReminderWhen.DayBefore, new(9, 0))])).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(UtcToday.ToDateTime(new(8, 0), DateTimeKind.Utc)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        var firstId = (await db.Notifications.SingleAsync()).Id;
        clock.SetUtcNow(new(UtcToday.ToDateTime(new(9, 0), DateTimeKind.Utc)));
        await service.CheckAndSendReminders(default);

        var reminder = await db.Notifications.SingleAsync();
        reminder.Id.Should().Be(firstId);
        reminder.ReminderDate.Should().Be(UtcToday.AddDays(1));
        reminder.Body.Should().Be("Due tomorrow");
        await ApplyInboxActionAsync(db, habit, UtcToday, TimeZoneInfo.Utc, "log");
        db.ChangeTracker.Clear();
        (await db.Notifications.SingleAsync()).IsRead.Should().BeFalse();
        await ApplyInboxActionAsync(db, habit, UtcToday.AddDays(1), TimeZoneInfo.Utc, "log");
        db.ChangeTracker.Clear();
        (await db.Notifications.SingleAsync()).IsRead.Should().BeTrue();
    }

    private static async Task ApplyInboxActionAsync(OrbitDbContext db, Habit habit, DateOnly date, TimeZoneInfo zone, string action)
    {
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(habit.UserId, Arg.Any<CancellationToken>()).Returns(date);
        dates.GetUserTimeZoneAsync(habit.UserId, Arg.Any<CancellationToken>()).Returns(zone);
        var habits = new GenericRepository<Habit>(db);
        var logs = new GenericRepository<HabitLog>(db);
        var notifications = new GenericRepository<Notification>(db);
        var unit = new UnitOfWork(db, new DatabaseConnectionSettings());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gamification = Substitute.For<IGamificationService>();
        var goals = new GoalCompletionService(new GenericRepository<Goal>(db), gamification, unit, dates);
        var streaks = Substitute.For<IUserStreakService>();
        switch (action)
        {
            case "log":
                var log = new LogHabitCommandHandler(new(habits, logs, new GenericRepository<User>(db),
                        new GenericRepository<SentReminder>(db), notifications),
                    new(dates, streaks, gamification, goals, Substitute.For<IMediator>(), Substitute.For<IPayGateService>()),
                    unit, cache, NullLogger<LogHabitCommandHandler>.Instance);
                (await log.Handle(new(habit.UserId, habit.Id, date, ToggleExisting: false), default)).IsSuccess.Should().BeTrue();
                break;
            case "bulk-log":
                var bulkLog = new BulkLogHabitsCommandHandler(habits, logs, new(dates, streaks, gamification, notifications),
                    goals, unit, cache, NullLogger<BulkLogHabitsCommandHandler>.Instance);
                (await bulkLog.Handle(new(habit.UserId, [new(habit.Id, date)]), default)).Value.Results.Single()
                    .Status.Should().Be(BulkItemStatus.Success);
                break;
            case "skip":
                var skip = new SkipHabitCommandHandler(new(habits, logs, new GenericRepository<HabitSkipUndo>(db), notifications),
                    dates, goals, unit, cache);
                (await skip.Handle(new(habit.UserId, habit.Id, date), default)).IsSuccess.Should().BeTrue();
                break;
            case "bulk-skip":
                var bulkSkip = new BulkSkipHabitsCommandHandler(habits, logs, dates, unit, cache, notifications);
                (await bulkSkip.Handle(new(habit.UserId, [new(habit.Id, date)]), default)).Value.Results.Single()
                    .Status.Should().Be(BulkItemStatus.Success);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }
}
