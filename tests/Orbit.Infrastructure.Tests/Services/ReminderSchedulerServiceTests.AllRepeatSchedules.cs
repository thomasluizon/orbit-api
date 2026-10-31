using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Configuration;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Services;

public partial class ReminderSchedulerServiceTests
{
    [Theory]
    [InlineData(true, 10000)]
    [InlineData(false, 10000)]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    public async Task Repeat_SparseScheduleCannotHoldUpReadbackOrOtherReminders(bool readback, int years)
    {
        var db = CreateSqliteDbContext();
        var (active, startAtUtc) = await SeedRepeat();
        var sparse = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, active.UserId, "Sparse",
            FrequencyUnit.Year, years, UtcToday, ReminderEnabled: true,
            RepeatReminder: new(480, StartAtUtc: startAtUtc))).Value;
        var log = sparse.Log(UtcToday, loggedOnLocalDate: UtcToday);
        log.IsSuccess.Should().BeTrue();
        db.Habits.Add(sparse);
        db.HabitLogs.Add(log.Value);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(startAtUtc.AddHours(8)));
        if (readback)
        {
            var next = await Task.Run(() => ReadNextReminder(sparse, clock)).WaitAsync(TimeSpan.FromSeconds(5));
            if (years == 1)
                next.Should().Be(UtcToday.AddYears(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            else
                next.Should().BeNull();
        }
        else
        {
            var push = Substitute.For<IPushNotificationService>();
            var scheduler = CreateService(db, push, clock);
            await Task.Run(() => scheduler.CheckAndSendReminders(default)).WaitAsync(TimeSpan.FromSeconds(5));
            (await db.SentReminders.ToListAsync()).Should().ContainSingle().Which.HabitId.Should().Be(active.Id);
            await push.Received(1).SendReminderToUserAsync(active.UserId, active.Title, Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(), "/", Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime, null, null, 0, false, true)]
    [InlineData(HabitSchedule.OneTime, null, null, -1, false, true)]
    [InlineData(HabitSchedule.OneTime, null, null, 1, false, false)]
    [InlineData(HabitSchedule.Recurring, FrequencyUnit.Week, 1, 0, false, true)]
    [InlineData(HabitSchedule.Recurring, FrequencyUnit.Week, 1, -1, false, true)]
    [InlineData(HabitSchedule.Recurring, FrequencyUnit.Week, 1, -1, true, false)]
    [InlineData(HabitSchedule.Recurring, FrequencyUnit.Day, 2, -1, false, true)]
    [InlineData(HabitSchedule.Flexible, FrequencyUnit.Week, 3, 0, false, true)]
    [InlineData(HabitSchedule.General, null, null, 0, false, true)]
    public async Task Repeat_EveryScheduleUsesTheSameDispatchAndNextInstant(HabitSchedule schedule,
        FrequencyUnit? unit, int? quantity, int dueOffset, bool resolvedDue, bool sends)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        var startAtUtc = UtcToday.ToDateTime(new(4, 0), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(schedule, user.Id, "Reminder", unit, quantity,
            UtcToday.AddDays(dueOffset), ReminderEnabled: true, RepeatReminder: new(480, StartAtUtc: startAtUtc))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        if (resolvedDue)
            db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, habit.NextDueDate,
                0, 0, startAtUtc.AddDays(dueOffset), loggedOnLocalDate: habit.NextDueDate));
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(startAtUtc.AddHours(8)));
        var next = await ReadNextReminder(habit, clock);
        if (sends)
            next.Should().Be(clock.GetUtcNow().UtcDateTime);
        else
            next.Should().BeAfter(clock.GetUtcNow().UtcDateTime);
        var push = Substitute.For<IPushNotificationService>();
        var scheduler = CreateService(db, push, clock);
        await scheduler.CheckAndSendReminders(default);
        await scheduler.CheckAndSendReminders(default);
        var reminders = await db.SentReminders.ToListAsync();
        reminders.Should().HaveCount(sends ? 1 : 0);
        if (sends)
        {
            reminders.Single().ReminderAtUtc.Should().Be(next);
            await push.Received(1).SendReminderToUserAsync(user.Id, habit.Title, Arg.Any<string>(),
                Arg.Is<IReadOnlyDictionary<string, string>>(data => data.ContainsKey("doneToken") && data["doneLabel"] == "Done"),
                "/", Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(HabitSchedule.Recurring, 0)]
    [InlineData(HabitSchedule.Recurring, 1)]
    [InlineData(HabitSchedule.Flexible, 0)]
    [InlineData(HabitSchedule.Flexible, 1)]
    [InlineData(HabitSchedule.Flexible, 8)]
    [InlineData(HabitSchedule.OneTime, 0)]
    [InlineData(HabitSchedule.OneTime, 1)]
    [InlineData(HabitSchedule.General, 0)]
    public async Task Repeat_DoneOrSkippedTodaySuppressesCachedFollowUps(HabitSchedule schedule, int value)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        var startAtUtc = UtcToday.ToDateTime(new(4, 0), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(schedule, user.Id, "Reminder",
            schedule is HabitSchedule.OneTime or HabitSchedule.General ? null : FrequencyUnit.Day,
            schedule is HabitSchedule.OneTime or HabitSchedule.General ? null : 8, UtcToday, ReminderEnabled: true,
            RepeatReminder: new(480, StartAtUtc: startAtUtc))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(startAtUtc.AddHours(8)));
        var scheduler = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await scheduler.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(1);
        if (value > 0)
        {
            for (var i = 0; i < value; i++)
            {
                var log = habit.Log(UtcToday, loggedOnLocalDate: UtcToday);
                log.IsSuccess.Should().BeTrue();
                log.Value.CorrectTakenAtUtc(clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().UtcDateTime);
                db.HabitLogs.Add(log.Value);
            }
            await db.SaveChangesAsync();
        }
        else
        {
            var dates = Substitute.For<IUserDateService>();
            dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(UtcToday);
            dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var skip = new SkipHabitCommandHandler(new(new GenericRepository<Habit>(db),
                    new GenericRepository<HabitLog>(db), new GenericRepository<HabitSkipUndo>(db),
                    new GenericRepository<Notification>(db)),
                dates, Substitute.For<IGoalCompletionService>(), new UnitOfWork(db, new DatabaseConnectionSettings()), cache);
            (await skip.Handle(new(user.Id, habit.Id), default)).IsSuccess.Should().BeTrue();
        }
        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(15));
        var next = await ReadNextReminder(habit, clock);
        var partialFlexible = schedule == HabitSchedule.Flexible && value == 1;
        if (partialFlexible)
            next.Should().Be(startAtUtc.AddHours(16));
        else
            (next is null || next > UtcToday.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddTicks(-1))
                .Should().BeTrue();
        await scheduler.CheckAndSendReminders(default);
        clock.SetUtcNow(new(startAtUtc.AddHours(16)));
        await scheduler.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(partialFlexible ? 2 : 1);
        if (schedule == HabitSchedule.General)
        {
            clock.SetUtcNow(new(startAtUtc.AddDays(1)));
            (await ReadNextReminder(habit, clock)).Should().Be(clock.GetUtcNow().UtcDateTime);
            await scheduler.CheckAndSendReminders(default);
            (await db.SentReminders.CountAsync()).Should().Be(2);
        }
    }

    [Fact]
    public async Task Repeat_FlexibleWindowWithItsTargetMetWaitsForTheNextWindow()
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        var today = UtcToday.AddDays(3);
        var startAtUtc = today.ToDateTime(new(4, 0), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id, "Reminder",
            FrequencyUnit.Week, 2, UtcToday, ReminderEnabled: true,
            RepeatReminder: new(480, StartAtUtc: startAtUtc))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        for (var i = 1; i <= 2; i++)
            db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, today.AddDays(-i), 1, 0,
                startAtUtc.AddDays(-i), loggedOnLocalDate: today.AddDays(-i)));
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(startAtUtc.AddHours(8)));
        (await ReadNextReminder(habit, clock)).Should().BeAfter(startAtUtc.AddDays(4));
        await CreateService(db, Substitute.For<IPushNotificationService>(), clock).CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(0);
    }
}
