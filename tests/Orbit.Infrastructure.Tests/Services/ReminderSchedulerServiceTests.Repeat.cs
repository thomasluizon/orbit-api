using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public partial class ReminderSchedulerServiceTests
{
    [Fact]
    public void Repeat_LatestDoseQueryTranslatesWithPostgresWithoutConnecting()
    {
        using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=unused;Database=translation_only").Options);

        var habitId = Guid.NewGuid();
        var sql = RepeatReminderDoseLoader.LatestCandidates(db.HabitLogs.AsNoTracking().Where(l => l.HabitId == habitId), [])
            .ToQueryString();

        sql.Should().Contain("HabitLogs");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeat_HistoricalLogsLoadOnlyTheLatestQualifyingDose(bool legacy)
    {
        var reads = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetTimeZone("America/Sao_Paulo");
        var doseAtUtc = UtcToday.ToDateTime(new(2, 0), DateTimeKind.Utc);
        var localDate = UtcToday.AddDays(-1);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id, "Water", FrequencyUnit.Day, 1,
            localDate.AddDays(-400), ReminderEnabled: true, RepeatReminder: new(480, StartAtUtc: doseAtUtc.AddDays(-400)))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        db.HabitLogs.AddRange(Enumerable.Range(0, 400).Select(day => HabitLog.FromScheduleRead(
            Guid.NewGuid(), habit.Id, localDate.AddDays(-day), 1, 0, doseAtUtc.AddDays(-day),
            loggedOnLocalDate: legacy ? null : localDate.AddDays(-day))));
        db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, localDate.AddDays(-500), 1, 0,
            doseAtUtc.AddHours(1), loggedOnLocalDate: legacy ? null : localDate));
        await db.SaveChangesAsync();
        reads.Reset();

        await CreateService(db, Substitute.For<IPushNotificationService>(),
            new MutableTimeProvider(new(doseAtUtc.AddHours(8)))).CheckAndSendReminders(default);

        reads.Commands.Where(c => c.Sql.Contains("FROM \"HabitLogs\"", StringComparison.Ordinal))
            .Sum(c => c.Rows).Should().BeLessThanOrEqualTo(3);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(doseAtUtc.AddHours(8));
    }

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
        var (habit, start) = await SeedRepeat(schedule: HabitSchedule.General);
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
    public async Task Repeat_TimeZoneChangeDoesNotReclassifyAnActualDoseAsABackfill()
    {
        var db = CreateSqliteDbContext();
        var (habit, startAtUtc) = await SeedRepeat(schedule: HabitSchedule.General);
        var user = await db.Users.SingleAsync();
        var doseAtUtc = UtcToday.ToDateTime(new(1, 0), DateTimeKind.Utc);
        var log = HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, UtcToday, 1, 0, doseAtUtc, doseAtUtc,
            loggedOnLocalDate: UtcToday);
        db.HabitLogs.Add(log);
        await db.SaveChangesAsync();
        user.SetTimeZone("America/Sao_Paulo");
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(doseAtUtc.AddHours(8)));
        await CreateService(db, Substitute.For<IPushNotificationService>(), clock).CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(doseAtUtc.AddHours(8));
    }

    [Fact]
    public async Task Repeat_TimeZoneChangeDoesNotTurnABackfillIntoTheClockAnchor()
    {
        var db = CreateSqliteDbContext();
        var (habit, startAtUtc) = await SeedRepeat();
        var user = await db.Users.SingleAsync();
        var createdAtUtc = UtcToday.ToDateTime(new(1, 0), DateTimeKind.Utc);
        db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, UtcToday.AddDays(-1), 1, 0,
            createdAtUtc, createdAtUtc, loggedOnLocalDate: UtcToday));
        user.SetTimeZone("America/Sao_Paulo");
        await db.SaveChangesAsync();
        await CreateService(db, Substitute.For<IPushNotificationService>(), new MutableTimeProvider(new(startAtUtc.AddHours(8))))
            .CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(startAtUtc.AddHours(8));
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
    [InlineData(HabitSchedule.General, 0)]
    public async Task Repeat_ScheduledHabitsStopAfterDailyCompletion(HabitSchedule schedule, int expected)
    {
        var db = CreateSqliteDbContext();
        var (habit, start) = await SeedRepeat(schedule: schedule, frequencyQuantity: 8);
        var log = habit.Log(UtcToday).Value;
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

    [Fact]
    public async Task Repeat_ActualDoseAtTwentyIsFollowedAtFourTheNextDay()
    {
        var db = CreateSqliteDbContext();
        var (habit, start) = await SeedRepeat();
        var doseAtUtc = UtcToday.ToDateTime(new(20, 0), DateTimeKind.Utc);
        db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, UtcToday, 1, 0, doseAtUtc, doseAtUtc));
        await db.SaveChangesAsync();
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), new MutableTimeProvider(new(doseAtUtc.AddHours(8))));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(doseAtUtc.AddHours(8));
    }

    [Fact]
    public async Task Repeat_SubHourIntervalHasNoFollowUp()
    {
        var db = CreateSqliteDbContext();
        var startAtUtc = UtcToday.ToDateTime(new(4, 0), DateTimeKind.Utc);
        await SeedRepeat(new(30, StartAtUtc: startAtUtc));
        var clock = new MutableTimeProvider(new(startAtUtc.AddMinutes(30)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        clock.SetUtcNow(new(startAtUtc.AddMinutes(45)));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(1);
        clock.SetUtcNow(new(startAtUtc.AddMinutes(60)));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(2);
        (await db.SentReminders.AnyAsync(r => r.ParentReminderId != null)).Should().BeFalse();
        clock.SetUtcNow(new(startAtUtc.AddMinutes(90)));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(3);
        var notification = (await db.Notifications.ToListAsync()).Should().ContainSingle().Subject;
        notification.IsRead.Should().BeFalse();
        notification.Body.Should().Be("Next at 06:00");
        notification.CreatedAtUtc.Should().Be(startAtUtc.AddMinutes(90));
    }

    [Fact]
    public async Task Repeat_RecurringResumesTheNextLocalDay()
    {
        var db = CreateSqliteDbContext();
        var (habit, startAtUtc) = await SeedRepeat(schedule: HabitSchedule.Recurring);
        var doseAtUtc = UtcToday.ToDateTime(new(12, 0), DateTimeKind.Utc);
        db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, UtcToday, 1, 0, doseAtUtc, doseAtUtc));
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(doseAtUtc.AddHours(8)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(0);
        clock.SetUtcNow(new(doseAtUtc.AddHours(16)));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(doseAtUtc.AddHours(16));
    }

    [Fact]
    public async Task Repeat_IntervalEditInvalidatesTheCachedSnapshot()
    {
        var db = CreateSqliteDbContext();
        var (habit, startAtUtc) = await SeedRepeat();
        var clock = new MutableTimeProvider(new(startAtUtc.AddHours(1)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(0);
        habit.Update(new HabitUpdateParams(habit.Schedule, habit.Title, habit.Description, habit.FrequencyUnit,
            habit.FrequencyQuantity, [], false, habit.NextDueDate, RepeatReminder: new(60))).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.SingleAsync()).ReminderAtUtc.Should().Be(startAtUtc.AddHours(1));
    }

    [Fact]
    public async Task Repeat_EndDateUsesTheUsersLocalDayAndSuppressesFollowUp()
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetTimeZone("America/Sao_Paulo");
        var reminderAtUtc = UtcToday.AddDays(1).ToDateTime(new(2, 50), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id, "Drink water", FrequencyUnit.Day, 1,
            UtcToday, EndDate: UtcToday, ReminderEnabled: true, RepeatReminder: new(480, StartAtUtc: reminderAtUtc.AddHours(-8)))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(reminderAtUtc));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(1);
        clock.SetUtcNow(new(reminderAtUtc.AddMinutes(15)));
        await service.CheckAndSendReminders(default);
        clock.SetUtcNow(new(reminderAtUtc.AddHours(8)));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("same-day")]
    [InlineData("day-before")]
    [InlineData("repeat")]
    public async Task EndDate_AllReminderKindsSendOnTheLastDayAndStopAtLocalMidnight(string kind)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetTimeZone("America/Sao_Paulo");
        var lastDayAtUtc = UtcToday.AddDays(kind == "day-before" ? 0 : 1).ToDateTime(new(2, 50), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Drink water", FrequencyUnit.Day, 1,
            UtcToday.AddDays(-1), ReminderEnabled: true, EndDate: UtcToday,
            DueTime: kind == "relative" ? new(23, 50) : null,
            ReminderTimes: kind == "relative" ? [0] : null,
            RelativeReminders: kind == "relative" ? [new(-20)] : null,
            ScheduledReminders: kind is "same-day" or "day-before"
                ? [new(kind == "same-day" ? ScheduledReminderWhen.SameDay : ScheduledReminderWhen.DayBefore, new(23, 50)),
                   new(kind == "same-day" ? ScheduledReminderWhen.SameDay : ScheduledReminderWhen.DayBefore, new(0, 10))] : null,
            RepeatReminder: kind == "repeat" ? new(480, StartAtUtc: lastDayAtUtc.AddHours(-8)) : null)).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var clock = new MutableTimeProvider(new(lastDayAtUtc));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        var lastDayCount = await db.SentReminders.CountAsync();
        lastDayCount.Should().BeGreaterThan(0);
        clock.SetUtcNow(new(UtcToday.AddDays(1).ToDateTime(new(3, 10), DateTimeKind.Utc)));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(lastDayCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeat_CompletedOrDeletedHabitStopsEvenWithACachedSnapshot(bool deleted)
    {
        var db = CreateSqliteDbContext();
        var (habit, startAtUtc) = await SeedRepeat();
        var clock = new MutableTimeProvider(new(startAtUtc.AddHours(8)));
        var service = CreateService(db, Substitute.For<IPushNotificationService>(), clock);
        await service.CheckAndSendReminders(default);
        if (deleted)
            habit.SoftDelete();
        else
        {
            habit.Update(new HabitUpdateParams(habit.Schedule, habit.Title, habit.Description, habit.FrequencyUnit,
                habit.FrequencyQuantity, [], false, habit.NextDueDate, EndDate: UtcToday));
            habit.AdvanceDueDate(UtcToday);
            habit.IsCompleted.Should().BeTrue();
        }
        await db.SaveChangesAsync();
        clock.SetUtcNow(new(startAtUtc.AddHours(8).AddMinutes(15)));
        await service.CheckAndSendReminders(default);
        clock.SetUtcNow(new(startAtUtc.AddHours(16)));
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("en", false, "Next at 4:00 AM", "Done")]
    [InlineData("en", true, "Next at 04:00", "Done")]
    [InlineData("pt-BR", true, "Próximo às 04:00", "Concluir")]
    public async Task Repeat_PayloadUsesTheUsersLanguageClockAndSignedReminderInstant(string language, bool clock24,
        string body, string doneLabel)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetLanguage(language);
        user.SetClockFormat(clock24);
        var reminderAtUtc = UtcToday.ToDateTime(new(20, 0), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.General, user.Id, "Drink water", null, null,
            UtcToday, ReminderEnabled: true, RepeatReminder: new(480, StartAtUtc: reminderAtUtc.AddHours(-8)))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var push = Substitute.For<IPushNotificationService>();
        await CreateService(db, push, new MutableTimeProvider(new(reminderAtUtc))).CheckAndSendReminders(default);
        await push.Received(1).SendReminderToUserAsync(user.Id, habit.Title, body,
            Arg.Is<IReadOnlyDictionary<string, string>>(data => data["habitId"] == habit.Id.ToString()
                && data["reminderAtUtc"] == reminderAtUtc.ToString("O") && data["doneLabel"] == doneLabel
                && !string.IsNullOrEmpty(data["doneToken"])), "/", Arg.Any<CancellationToken>());
    }

    private async Task<(Habit Habit, DateTime Start)> SeedRepeat(RepeatReminderOptions? options = null,
        HabitSchedule schedule = HabitSchedule.Flexible, int frequencyQuantity = 1)
    {
        var db = CreateSqliteDbContext();
        var user = User.Create("Alex", "alex@test.com").Value;
        var start = UtcToday.ToDateTime(new(4, 0), DateTimeKind.Utc);
        var habit = Habit.Create(new HabitCreateParams(schedule, user.Id, "Drink water",
            schedule == HabitSchedule.General ? null : FrequencyUnit.Day,
            schedule == HabitSchedule.General ? null : frequencyQuantity, UtcToday,
            ReminderEnabled: true, RepeatReminder: options ?? new(480, StartAtUtc: start))).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        return (habit, start);
    }
}
