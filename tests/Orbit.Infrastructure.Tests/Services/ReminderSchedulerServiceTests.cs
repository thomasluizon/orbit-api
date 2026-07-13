using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class ReminderSchedulerServiceTests
{
    private static readonly DateOnly UtcToday = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly int[] ReminderTimes = new[] { 0 };
    private static string FormatReminderText(int minutesBefore, string lang)
    {
        var method = typeof(ReminderSchedulerService)
            .GetMethod("FormatReminderText", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [minutesBefore, lang])!;
    }

    private static string FormatScheduledReminderText(ScheduledReminderWhen when, string lang)
    {
        var method = typeof(ReminderSchedulerService)
            .GetMethod("FormatScheduledReminderText", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [when, lang])!;
    }

    private static bool ShouldSendScheduledReminder(
        ScheduledReminderTime sr, bool isDueToday, bool isDueTomorrow, TimeOnly userTimeNow)
    {
        var method = typeof(ReminderSchedulerService)
            .GetMethod("ShouldSendScheduledReminder", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [sr, isDueToday, isDueTomorrow, userTimeNow])!;
    }

    private static string Pluralize(string singular, int count)
    {
        var method = typeof(ReminderSchedulerService)
            .GetMethod("Pluralize", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [singular, count])!;
    }

    [Fact]
    public void FormatReminderText_Zero_English_ReturnsDueNow()
    {
        var result = FormatReminderText(0, "en");
        result.Should().Be("Due now");
    }

    [Fact]
    public void FormatReminderText_Zero_Portuguese_ReturnsAgora()
    {
        var result = FormatReminderText(0, "pt-BR");
        result.Should().Be("Agora");
    }

    [Fact]
    public void FormatReminderText_Minutes_English()
    {
        var result = FormatReminderText(15, "en");
        result.Should().Be("Due in 15 minutes");
    }

    [Fact]
    public void FormatReminderText_Minutes_Portuguese()
    {
        var result = FormatReminderText(1, "pt-BR");
        result.Should().Be("Em 1 minuto");
    }

    [Fact]
    public void FormatReminderText_MultipleMinutes_Portuguese()
    {
        var result = FormatReminderText(30, "pt-BR");
        result.Should().Be("Em 30 minutos");
    }

    [Fact]
    public void FormatReminderText_Hours_English()
    {
        var result = FormatReminderText(120, "en");
        result.Should().Be("Due in 2 hours");
    }

    [Fact]
    public void FormatReminderText_OneHour_English()
    {
        var result = FormatReminderText(60, "en");
        result.Should().Be("Due in 1 hour");
    }

    [Fact]
    public void FormatReminderText_Hours_Portuguese()
    {
        var result = FormatReminderText(60, "pt-BR");
        result.Should().Be("Em 1 hora");
    }

    [Fact]
    public void FormatReminderText_MultipleHours_Portuguese()
    {
        var result = FormatReminderText(180, "pt-BR");
        result.Should().Be("Em 3 horas");
    }

    [Fact]
    public void FormatReminderText_Days_English()
    {
        var result = FormatReminderText(1440, "en");
        result.Should().Be("Due in 1 day");
    }

    [Fact]
    public void FormatReminderText_MultipleDays_English()
    {
        var result = FormatReminderText(2880, "en");
        result.Should().Be("Due in 2 days");
    }

    [Fact]
    public void FormatReminderText_Days_Portuguese()
    {
        var result = FormatReminderText(1440, "pt-BR");
        result.Should().Be("Em 1 dia");
    }

    [Fact]
    public void FormatReminderText_MultipleDays_Portuguese()
    {
        var result = FormatReminderText(4320, "pt-BR");
        result.Should().Be("Em 3 dias");
    }

    [Fact]
    public void FormatScheduledReminderText_SameDay_English()
    {
        var result = FormatScheduledReminderText(ScheduledReminderWhen.SameDay, "en");
        result.Should().Be("Due today");
    }

    [Fact]
    public void FormatScheduledReminderText_SameDay_Portuguese()
    {
        var result = FormatScheduledReminderText(ScheduledReminderWhen.SameDay, "pt-BR");
        result.Should().Be("Para hoje");
    }

    [Fact]
    public void FormatScheduledReminderText_DayBefore_English()
    {
        var result = FormatScheduledReminderText(ScheduledReminderWhen.DayBefore, "en");
        result.Should().Be("Due tomorrow");
    }

    [Fact]
    public void FormatScheduledReminderText_DayBefore_Portuguese()
    {
        var result = FormatScheduledReminderText(ScheduledReminderWhen.DayBefore, "pt-BR");
        result.Should().Be("Para amanh\u00e3");
    }

    [Fact]
    public void FormatScheduledReminderText_Unknown_English()
    {
        var result = FormatScheduledReminderText((ScheduledReminderWhen)99, "en");
        result.Should().Be("Reminder");
    }

    [Fact]
    public void FormatScheduledReminderText_Unknown_Portuguese()
    {
        var result = FormatScheduledReminderText((ScheduledReminderWhen)99, "pt-BR");
        result.Should().Be("Lembrete");
    }

    [Fact]
    public void ShouldSendScheduledReminder_SameDay_DueToday_WithinWindow_ReturnsTrue()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: true, isDueTomorrow: false, new TimeOnly(9, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSendScheduledReminder_SameDay_NotDueToday_ReturnsFalse()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: false, isDueTomorrow: true, new TimeOnly(9, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldSendScheduledReminder_DayBefore_DueTomorrow_WithinWindow_ReturnsTrue()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(20, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: false, isDueTomorrow: true, new TimeOnly(20, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSendScheduledReminder_DayBefore_NotDueTomorrow_ReturnsFalse()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(20, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: true, isDueTomorrow: false, new TimeOnly(20, 0));
        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldSendScheduledReminder_PastReminderTime_ReturnsTrue()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: true, isDueTomorrow: false, new TimeOnly(9, 2));
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSendScheduledReminder_LongAfterReminderTime_ReturnsTrue()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: true, isDueTomorrow: false, new TimeOnly(14, 0));
        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldSendScheduledReminder_BeforeReminderTime_ReturnsFalse()
    {
        var sr = new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0));
        var result = ShouldSendScheduledReminder(sr, isDueToday: true, isDueTomorrow: false, new TimeOnly(8, 59));
        result.Should().BeFalse();
    }

    [Fact]
    public void Pluralize_Single_ReturnsSingular()
    {
        Pluralize("minute", 1).Should().Be("minute");
    }

    [Fact]
    public void Pluralize_Multiple_ReturnsPlural()
    {
        Pluralize("minute", 5).Should().Be("minutes");
    }

    [Fact]
    public void Pluralize_Zero_ReturnsPlural()
    {
        Pluralize("hour", 0).Should().Be("hour");
    }

    [Fact]
    public async Task CheckAndSendReminders_TwoSameDayScheduledReminders_PersistsBothWithoutUniqueViolation()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Drink water", FrequencyUnit.Day, 1,
            ReminderEnabled: true,
            DueDate: UtcToday,
            ScheduledReminders: new List<ScheduledReminderTime>
            {
                new(ScheduledReminderWhen.SameDay, new TimeOnly(0, 0)),
                new(ScheduledReminderWhen.SameDay, new TimeOnly(0, 1))
            })).Value;

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        var sent = await dbContext.SentReminders.Where(r => r.HabitId == habit.Id).ToListAsync();
        sent.Should().HaveCount(2);
        sent.Select(r => r.ReminderTimeUtc).Should().BeEquivalentTo(
            new TimeOnly?[] { new TimeOnly(0, 0), new TimeOnly(0, 1) });
        await pushService.Received(2).SendToUserAsync(
            user.Id, habit.Title, Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_DayBeforeScheduledReminder_KeysOnSendDateNotDueDate()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var dueTomorrow = UtcToday.AddDays(1);
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Submit report", null, null,
            ReminderEnabled: true,
            DueDate: dueTomorrow,
            ScheduledReminders: new List<ScheduledReminderTime>
            {
                new(ScheduledReminderWhen.DayBefore, new TimeOnly(0, 0))
            })).Value;

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        var sent = await dbContext.SentReminders.Where(r => r.HabitId == habit.Id).ToListAsync();
        sent.Should().ContainSingle();
        sent[0].Date.Should().Be(UtcToday);
        sent[0].ReminderTimeUtc.Should().Be(new TimeOnly(0, 0));
        await pushService.Received(1).SendToUserAsync(
            user.Id, habit.Title, Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_DayBeforeAlreadySentYesterdayForTodayDue_DoesNotBlockSameDay()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var reminderTime = new TimeOnly(0, 0);
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Submit report", null, null,
            ReminderEnabled: true,
            DueDate: UtcToday,
            ScheduledReminders: new List<ScheduledReminderTime>
            {
                new(ScheduledReminderWhen.SameDay, reminderTime)
            })).Value;

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.SentReminders.Add(SentReminder.Create(habit.Id, UtcToday.AddDays(-1), 0, reminderTime));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        var sent = await dbContext.SentReminders
            .Where(r => r.HabitId == habit.Id && r.Date == UtcToday)
            .ToListAsync();
        sent.Should().ContainSingle();
        await pushService.Received(1).SendToUserAsync(
            user.Id, habit.Title, Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_SameDayAndDayBeforeSameTime_PersistsBothWithoutCollision()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var reminderTime = new TimeOnly(0, 0);
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Drink water", FrequencyUnit.Day, 1,
            ReminderEnabled: true,
            DueDate: UtcToday,
            ScheduledReminders: new List<ScheduledReminderTime>
            {
                new(ScheduledReminderWhen.SameDay, reminderTime),
                new(ScheduledReminderWhen.DayBefore, reminderTime)
            })).Value;

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        var sent = await dbContext.SentReminders
            .Where(r => r.HabitId == habit.Id && r.Date == UtcToday)
            .ToListAsync();
        sent.Should().HaveCount(2);
        sent.Select(r => r.When).Should().BeEquivalentTo(
            new ScheduledReminderWhen?[] { ScheduledReminderWhen.SameDay, ScheduledReminderWhen.DayBefore });
        await pushService.Received(2).SendToUserAsync(
            user.Id, habit.Title, Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_ScheduledReminderAlreadySent_DoesNotResend()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var reminderTime = new TimeOnly(0, 0);
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Drink water", FrequencyUnit.Day, 1,
            ReminderEnabled: true,
            DueDate: UtcToday,
            ScheduledReminders: new List<ScheduledReminderTime>
            {
                new(ScheduledReminderWhen.SameDay, reminderTime)
            })).Value;

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.SentReminders.Add(SentReminder.Create(habit.Id, UtcToday, 0, reminderTime));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        (await dbContext.SentReminders.CountAsync(r => r.HabitId == habit.Id)).Should().Be(1);
        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_RelativeReminderDueButUnsent_FiresAndRecords()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Workout", FrequencyUnit.Day, 1,
            ReminderEnabled: true,
            DueDate: UtcToday,
            DueTime: new TimeOnly(0, 0),
            ReminderTimes: ReminderTimes)).Value;

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        var sent = await dbContext.SentReminders.Where(r => r.HabitId == habit.Id).ToListAsync();
        sent.Should().ContainSingle();
        sent[0].MinutesBefore.Should().Be(0);
        sent[0].ReminderTimeUtc.Should().BeNull();
        await pushService.Received(1).SendToUserAsync(
            user.Id, habit.Title, Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_RelativeReminderAlreadySent_DoesNotDoubleSend()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Workout", FrequencyUnit.Day, 1,
            ReminderEnabled: true,
            DueDate: UtcToday,
            DueTime: new TimeOnly(0, 0),
            ReminderTimes: ReminderTimes)).Value;

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.SentReminders.Add(SentReminder.Create(habit.Id, UtcToday, 0));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        (await dbContext.SentReminders.CountAsync(r => r.HabitId == habit.Id)).Should().Be(1);
        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_RelativeReminderAlreadySentOnUserLocalDate_DoesNotResendWhenLocalDiffersFromUtc()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var nowUtc = DateTime.UtcNow;
        var timeZoneId = nowUtc.TimeOfDay < TimeSpan.FromHours(12) ? "Etc/GMT+12" : "Etc/GMT-12";
        var userToday = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
        userToday.Should().NotBe(UtcToday);

        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetTimeZone(timeZoneId);
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Workout", FrequencyUnit.Day, 1,
            ReminderEnabled: true,
            DueDate: UtcToday.AddDays(-1),
            DueTime: new TimeOnly(0, 0),
            ReminderTimes: ReminderTimes)).Value;

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.SentReminders.Add(SentReminder.Create(habit.Id, userToday, 0));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        (await dbContext.SentReminders.CountAsync(r => r.HabitId == habit.Id)).Should().Be(1);
        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_RelativeReminderHabitLoggedOnUserLocalDate_DoesNotSendWhenLocalDiffersFromUtc()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var nowUtc = DateTime.UtcNow;
        var timeZoneId = nowUtc.TimeOfDay < TimeSpan.FromHours(12) ? "Etc/GMT+12" : "Etc/GMT-12";
        var userToday = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)));
        userToday.Should().NotBe(UtcToday);

        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetTimeZone(timeZoneId);
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Workout", FrequencyUnit.Day, 1,
            ReminderEnabled: true,
            DueDate: UtcToday.AddDays(-1),
            DueTime: new TimeOnly(0, 0),
            ReminderTimes: ReminderTimes)).Value;

        habit.Log(userToday, advanceDueDate: false);
        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        (await dbContext.SentReminders.CountAsync(r => r.HabitId == habit.Id)).Should().Be(0);
        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_RelativeReminderSentYesterday_DoesNotBlockToday()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Workout", FrequencyUnit.Day, 1,
            ReminderEnabled: true,
            DueDate: UtcToday.AddDays(-1),
            DueTime: new TimeOnly(0, 0),
            ReminderTimes: ReminderTimes)).Value;

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.SentReminders.Add(SentReminder.Create(habit.Id, UtcToday.AddDays(-1), 0));
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        var sentToday = await dbContext.SentReminders
            .Where(r => r.HabitId == habit.Id && r.Date == UtcToday).ToListAsync();
        sentToday.Should().ContainSingle();
        await pushService.Received(1).SendToUserAsync(
            user.Id, habit.Title, Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_ManyDueReminders_DeliversEachRecipientExactlyOnce()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var users = SeedDueRelativeReminders(dbContext, count: 6);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        (await dbContext.SentReminders.CountAsync()).Should().Be(users.Count);
        foreach (var user in users)
        {
            await pushService.Received(1).SendToUserAsync(
                user.Id, Arg.Any<string>(), Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
        }
        await pushService.Received(users.Count).SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAndSendReminders_OneRecipientPushThrows_OthersStillRecordedAndDelivered()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var users = SeedDueRelativeReminders(dbContext, count: 4);
        await dbContext.SaveChangesAsync();

        pushService.SendToUserAsync(
                users[0].Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("push down")));

        var service = CreateService(dbContext, pushService);
        await service.CheckAndSendReminders(CancellationToken.None);

        (await dbContext.SentReminders.CountAsync()).Should().Be(users.Count);
        foreach (var user in users.Skip(1))
        {
            await pushService.Received(1).SendToUserAsync(
                user.Id, Arg.Any<string>(), Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task ExecuteAsync_HostedLifecycle_RunsOneReminderPassThenStopsGracefully()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var pushService = Substitute.For<IPushNotificationService>();

        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Workout", FrequencyUnit.Day, 1,
            ReminderEnabled: true, DueDate: UtcToday, DueTime: new TimeOnly(0, 0),
            ReminderTimes: ReminderTimes)).Value;
        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync();

        var firstPush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pushService
            .SendToUserAsync(user.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { firstPush.TrySetResult(); return Task.CompletedTask; });

        var service = CreateService(dbContext, pushService);

        await service.StartAsync(CancellationToken.None);
        await firstPush.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        await pushService.Received(1).SendToUserAsync(
            user.Id, habit.Title, Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
        (await dbContext.SentReminders.CountAsync(r => r.HabitId == habit.Id)).Should().Be(1);
        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.IsCompleted.Should().BeTrue();
        service.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAndSendReminders_OneHabitRecordFails_StillDeliversRemainingHabits()
    {
        var throwingUser = User.Create("Alice", "alice@test.com").Value;
        var throwingHabit = Habit.Create(new HabitCreateParams(
            throwingUser.Id, "Alice workout", FrequencyUnit.Day, 1,
            ReminderEnabled: true, DueDate: UtcToday, DueTime: new TimeOnly(0, 0),
            ReminderTimes: ReminderTimes)).Value;
        var healthyUser = User.Create("Bob", "bob@test.com").Value;
        var healthyHabit = Habit.Create(new HabitCreateParams(
            healthyUser.Id, "Bob workout", FrequencyUnit.Day, 1,
            ReminderEnabled: true, DueDate: UtcToday, DueTime: new TimeOnly(0, 0),
            ReminderTimes: ReminderTimes)).Value;

        await using var dbContext = CreateInterceptingDbContext(
            new ThrowForHabitReminderInterceptor(throwingHabit.Id));
        var pushService = Substitute.For<IPushNotificationService>();

        dbContext.Users.AddRange(throwingUser, healthyUser);
        dbContext.Habits.AddRange(throwingHabit, healthyHabit);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, pushService);

        var run = async () => await service.CheckAndSendReminders(CancellationToken.None);
        await run.Should().NotThrowAsync();

        await pushService.Received(1).SendToUserAsync(
            healthyUser.Id, healthyHabit.Title, Arg.Any<string>(), "/", Arg.Any<CancellationToken>());
        await pushService.DidNotReceive().SendToUserAsync(
            throwingUser.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        (await dbContext.SentReminders.CountAsync(r => r.HabitId == healthyHabit.Id)).Should().Be(1);
        (await dbContext.SentReminders.CountAsync(r => r.HabitId == throwingHabit.Id)).Should().Be(0);
    }

    private static List<User> SeedDueRelativeReminders(OrbitDbContext dbContext, int count)
    {
        var users = new List<User>(count);
        for (var index = 0; index < count; index++)
        {
            var user = User.Create($"User{index}", $"user{index}@test.com").Value;
            var habit = Habit.Create(new HabitCreateParams(
                user.Id, $"Workout {index}", FrequencyUnit.Day, 1,
                ReminderEnabled: true,
                DueDate: UtcToday,
                DueTime: new TimeOnly(0, 0),
                ReminderTimes: ReminderTimes)).Value;
            dbContext.Users.Add(user);
            dbContext.Habits.Add(habit);
            users.Add(user);
        }
        return users;
    }

    private static OrbitDbContext CreateInMemoryDbContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"ReminderSchedulerServiceTests_{Guid.NewGuid()}")
            .Options);

    private static ReminderSchedulerService CreateService(
        OrbitDbContext dbContext, IPushNotificationService pushService)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(pushService)
            .BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new ReminderSchedulerService(
            scopeFactory, NullLogger<ReminderSchedulerService>.Instance,
            new ConfigurationBuilder().Build());
    }

    private static OrbitDbContext CreateInterceptingDbContext(ISaveChangesInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"ReminderSchedulerServiceTests_{Guid.NewGuid()}")
            .AddInterceptors(interceptor)
            .Options);

    private sealed class ThrowForHabitReminderInterceptor(Guid habitId) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var addsThrowingHabitReminder = eventData.Context?.ChangeTracker
                .Entries<SentReminder>()
                .Any(e => e.State == EntityState.Added && e.Entity.HabitId == habitId) == true;

            if (addsThrowingHabitReminder)
                throw new InvalidOperationException("reminder store unavailable");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
