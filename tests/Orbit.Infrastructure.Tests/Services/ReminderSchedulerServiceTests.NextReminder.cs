using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class ReminderSchedulerServiceTests
{
    [Fact]
    public async Task NextReminder_LegitimateFutureStartStillProjectsDelivery()
    {
        var startAtUtc = DefaultInstant.UtcDateTime.AddDays(10);
        var (habit, _) = await SeedRepeat(new(15, StartAtUtc: startAtUtc));
        (await ReadNextReminder(habit, new MutableTimeProvider(DefaultInstant)))
            .Should().Be(startAtUtc.AddMinutes(15));
    }

    [Fact]
    public async Task NextReminder_UnrepresentableExpiryReturnsNull()
    {
        var startAtUtc = new DateTime(9999, 12, 31, 23, 40, 0, DateTimeKind.Utc);
        var (habit, _) = await SeedRepeat(new(15, StartAtUtc: startAtUtc));
        (await ReadNextReminder(habit, new MutableTimeProvider(DefaultInstant))).Should().BeNull();
    }

    [Fact]
    public async Task NextReminder_OldFollowUpAgreesWithSchedulerSnapshot()
    {
        var db = CreateSqliteDbContext();
        var (habit, start) = await SeedRepeat();
        var dueAtUtc = start.AddHours(8);
        var parent = SentReminder.CreateRepeat(habit.Id, UtcToday, dueAtUtc, dueAtUtc.AddHours(8), start);
        db.SentReminders.AddRange(parent, SentReminder.CreateRepeat(habit.Id, UtcToday,
            dueAtUtc.AddMinutes(15), dueAtUtc.AddHours(8), start, parent.Id));
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(dueAtUtc.AddDays(4)));
        var readAtUtc = await ReadNextReminder(habit, clock);
        readAtUtc.Should().Be(clock.GetUtcNow().UtcDateTime);
        await CreateService(db, Substitute.For<IPushNotificationService>(), clock).CheckAndSendReminders(default);
        var sent = await db.SentReminders.SingleAsync(r => r.ReminderAtUtc == readAtUtc);
        sent.ParentReminderId.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextReminder_UnrepresentablePersistedStartReturnsNull(bool activeWindow)
    {
        var (habit, _) = await SeedUnrepresentableRepeat(activeWindow);
        (await ReadNextReminder(habit, new MutableTimeProvider(DefaultInstant))).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeat_UnrepresentablePersistedStartDoesNotLoseAnotherUsersPush(bool activeWindow)
    {
        var db = CreateSqliteDbContext();
        await SeedUnrepresentableRepeat(activeWindow);
        var user = User.Create("Healthy", "healthy@test.com").Value;
        var healthy = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Workout",
            FrequencyUnit.Day, 1, UtcToday, DueTime: new(0, 0), ReminderEnabled: true,
            ReminderTimes: [0])).Value;
        db.Users.Add(user);
        db.Habits.Add(healthy);
        await db.SaveChangesAsync();
        var push = Substitute.For<IPushNotificationService>();
        var scheduler = CreateService(db, push);

        await scheduler.CheckAndSendReminders(default);
        await scheduler.CheckAndSendReminders(default);

        (await db.SentReminders.SingleAsync()).HabitId.Should().Be(healthy.Id);
        await push.Received(1).SendToUserAsync(user.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private async Task<(Habit Habit, DateTime Start)> SeedUnrepresentableRepeat(bool activeWindow)
    {
        var db = CreateSqliteDbContext();
        var seeded = await SeedRepeat(activeWindow ? new(15, new(8, 0), new(20, 0)) : new(15));
        db.Entry(seeded.Habit).Property(h => h.RepeatStartAtUtc).CurrentValue =
            new DateTime(9999, 12, 31, activeWindow ? 20 : 23, 50, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        return seeded;
    }

    [Theory]
    [InlineData(HabitSchedule.General)]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.Recurring)]
    public async Task NextReminder_CrossMidnightReadbackMatchesDelivery(HabitSchedule schedule)
    {
        var db = CreateSqliteDbContext();
        var (habit, _) = await SeedRepeat(schedule: schedule);
        var anchor = UtcToday.ToDateTime(new(20, 0), DateTimeKind.Utc);
        db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, UtcToday, 1, 0,
            anchor, anchor, loggedOnLocalDate: UtcToday));
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(anchor.AddHours(1)));
        var next = anchor.AddHours(8);

        (await ReadNextReminder(habit, clock)).Should().Be(next);
        clock.SetUtcNow(new(next));
        await CreateService(db, Substitute.For<IPushNotificationService>(), clock).CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(next);
    }

    [Fact]
    public async Task NextReminder_IncludesOneIgnoredFollowUpThenTheOriginalCadence()
    {
        var db = CreateSqliteDbContext();
        var (habit, start) = await SeedRepeat();
        var clock = new MutableTimeProvider(new(start.AddHours(8)));
        var scheduler = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        (await ReadNextReminder(habit, clock)).Should().Be(start.AddHours(8));
        await scheduler.CheckAndSendReminders(default);
        var followUp = start.AddHours(8).AddMinutes(15);
        (await ReadNextReminder(habit, clock)).Should().Be(followUp);
        clock.SetUtcNow(new(followUp));
        await scheduler.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(2);
        (await ReadNextReminder(habit, clock)).Should().Be(start.AddHours(16));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextReminder_TimeZoneChangePreservesOriginalDoseEligibility(bool backfill)
    {
        var db = CreateSqliteDbContext();
        var (habit, start) = await SeedRepeat();
        var doseAtUtc = UtcToday.ToDateTime(new(1, 0), DateTimeKind.Utc);
        db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id,
            backfill ? UtcToday.AddDays(-1) : UtcToday, 1, 0, doseAtUtc, doseAtUtc,
            loggedOnLocalDate: UtcToday));
        (await db.Users.SingleAsync()).SetTimeZone("America/Sao_Paulo");
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(UtcToday.ToDateTime(new(5, 0), DateTimeKind.Utc)));
        (await ReadNextReminder(habit, clock)).Should().Be((backfill ? start : doseAtUtc).AddHours(8));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextReminder_ActiveWindowResumesUnlessTheEndDateHasPassed(bool endsToday)
    {
        var db = CreateSqliteDbContext();
        var start = UtcToday.ToDateTime(new(20, 0), DateTimeKind.Utc);
        var (habit, _) = await SeedRepeat(new(120, new(22, 0), new(6, 0), start));
        if (endsToday)
            habit.Update(new HabitUpdateParams(habit.Schedule, habit.Title, habit.Description, habit.FrequencyUnit,
                habit.FrequencyQuantity, [], false, habit.NextDueDate, EndDate: UtcToday));
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(UtcToday.AddDays(1).ToDateTime(new(7, 0), DateTimeKind.Utc)));
        (await ReadNextReminder(habit, clock)).Should().Be(endsToday ? null
            : UtcToday.AddDays(1).ToDateTime(new(22, 0), DateTimeKind.Utc));
    }

    [Fact]
    public async Task NextReminder_RecurringCompletionStopsUntilTheNextLocalDay()
    {
        var db = CreateSqliteDbContext();
        var (habit, _) = await SeedRepeat(schedule: HabitSchedule.Recurring);
        (await db.Users.SingleAsync()).SetTimeZone("America/Sao_Paulo");
        var dose = UtcToday.ToDateTime(new(15, 0), DateTimeKind.Utc);
        db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, UtcToday, 1, 0, dose, dose,
            loggedOnLocalDate: UtcToday));
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(dose.AddHours(1)));
        var midnightAtUtc = UtcToday.AddDays(1).ToDateTime(new(3, 0), DateTimeKind.Utc);
        (await ReadNextReminder(habit, clock)).Should().Be(midnightAtUtc);
        clock.SetUtcNow(new(midnightAtUtc));
        await CreateService(db, Substitute.For<IPushNotificationService>(), clock).CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(dose.AddHours(8));
    }

    [Fact]
    public async Task NextReminder_DstGapKeepsTheElapsedCadence()
    {
        var db = CreateSqliteDbContext();
        var start = new DateTime(2027, 3, 14, 5, 0, 0, DateTimeKind.Utc);
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetTimeZone("America/New_York");
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id, "Water", FrequencyUnit.Day, 1,
            new(2027, 3, 14), ReminderEnabled: true, RepeatReminder: new(480, StartAtUtc: start))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        (await ReadNextReminder(habit, new MutableTimeProvider(new(start.AddHours(1))))).Should().Be(start.AddHours(8));
    }

    [Fact]
    public async Task NextReminder_NoRemainingWindowReturnsNull()
    {
        var db = CreateSqliteDbContext();
        var start = UtcToday.ToDateTime(new(20, 0), DateTimeKind.Utc);
        var (habit, _) = await SeedRepeat(new(480, new(8, 0), new(10, 0), start));
        habit.Update(new HabitUpdateParams(habit.Schedule, habit.Title, habit.Description, habit.FrequencyUnit,
            habit.FrequencyQuantity, [], false, habit.NextDueDate, EndDate: UtcToday));
        await db.SaveChangesAsync();
        (await ReadNextReminder(habit, new MutableTimeProvider(new(start)))).Should().BeNull();
    }

    private async Task<DateTime?> ReadNextReminder(Habit habit, MutableTimeProvider clock)
    {
        var db = CreateSqliteDbContext();
        var user = await db.Users.SingleAsync(u => u.Id == habit.UserId);
        var dates = Substitute.For<IUserDateService>();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone ?? "UTC");
        dates.GetUserTimeZoneAsync(user.Id, Arg.Any<CancellationToken>()).Returns(zone);
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, zone)));
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user.WeekStartDay);
        var result = await new GetHabitByIdQueryHandler(new GenericRepository<Habit>(db),
            new GenericRepository<HabitLog>(db), dates, repeatReminderReader: new Orbit.Infrastructure.Services.RepeatReminderReader(db, dates, clock)).Handle(new(user.Id, habit.Id), default);
        result.IsSuccess.Should().BeTrue();
        var json = JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.TryGetProperty("nextReminderAtUtc", out var next).Should().BeTrue();
        return next.ValueKind == JsonValueKind.Null ? null : next.GetDateTime();
    }
}
