using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Tests.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class SchedulerProjectionTests
{
    [Fact]
    public async Task ReminderTick_NoCandidates_ProbeStillReturnsOneRow()
    {
        var reads = new SchedulerReadInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var service = new ReminderSchedulerService(
            Scope(factory.Context, Substitute.For<IPushNotificationService>()),
            NullLogger<ReminderSchedulerService>.Instance, new ConfigurationBuilder().Build());

        await service.CheckAndSendReminders(CancellationToken.None);
        reads.Clear();
        await service.CheckAndSendReminders(CancellationToken.None);

        reads.Commands.Should().ContainSingle().Which.Should().Contain("COUNT(");
    }

    [Fact]
    public async Task ReminderTick_ProjectedReads_SendRelativeAndScheduledReminders()
    {
        var reads = new SchedulerReadInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var user = User.Create("Alex", "alex@test.com").Value;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var relative = Habit.Create(new HabitCreateParams(user.Id, "Relative", FrequencyUnit.Day, 1,
            today, DueTime: new TimeOnly(0, 0), ReminderEnabled: true, ReminderTimes: [0])).Value;
        var scheduled = Habit.Create(new HabitCreateParams(user.Id, "Scheduled", FrequencyUnit.Day, 1,
            today, ReminderEnabled: true, ScheduledReminders: [new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(0, 0))])).Value;
        db.Users.Add(user);
        db.Habits.AddRange(relative, scheduled);
        await db.SaveChangesAsync();
        reads.Clear();

        var push = Substitute.For<IPushNotificationService>();
        var service = new ReminderSchedulerService(Scope(db, push), NullLogger<ReminderSchedulerService>.Instance,
            new ConfigurationBuilder().Build());
        await service.CheckAndSendReminders(CancellationToken.None);

        await push.Received(1).SendToUserAsync(user.Id, "Relative", "Due now", "/", Arg.Any<CancellationToken>());
        await push.Received(1).SendToUserAsync(user.Id, "Scheduled", "Due today", "/", Arg.Any<CancellationToken>());
        reads.AssertNarrowHabitAndUserReads();

        user.SetName("Alex Updated").IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        reads.Clear();
        await service.CheckAndSendReminders(CancellationToken.None);
        reads.Commands.Should().ContainSingle();
        reads.Commands[0].Should().Contain("COUNT(").And.Contain("SUM(")
            .And.Contain("\"ReminderProbeVersion\"").And.Contain("\"ReminderPreferencesVersion\"")
            .And.NotContain("GROUP BY");
        reads.Commands[0].Should().NotContain("\"Title\"")
            .And.NotContain("\"HabitLogs\"")
            .And.NotContain("\"SentReminders\"");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("restore")]
    [InlineData("complete")]
    [InlineData("toggle")]
    public async Task ReminderTick_HabitChanges_RefreshCachedCandidates(string change)
    {
        var reads = new SchedulerReadInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Reminder", FrequencyUnit.Day, 1,
            today, ReminderEnabled: change != "toggle",
            EndDate: change == "complete" ? today : null,
            ScheduledReminders: [new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(0, 0))])).Value;
        if (change == "restore") habit.SoftDelete();
        db.Users.Add(user);
        if (change != "create") db.Habits.Add(habit);
        if (change is "create" or "delete")
        {
            var newer = Habit.Create(new HabitCreateParams(user.Id, "Later reminder", FrequencyUnit.Day, 1,
                today, ReminderEnabled: true,
                ScheduledReminders: [new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(12, 0))])).Value;
            db.Habits.Add(newer);
        }
        await db.SaveChangesAsync();
        if (change is "create" or "delete")
            await db.Habits.Where(h => h.Title == "Later reminder").ExecuteUpdateAsync(
                setters => setters.SetProperty(h => h.UpdatedAtUtc, DateTime.UtcNow.AddDays(1)));

        var service = new ReminderSchedulerService(Scope(db, Substitute.For<IPushNotificationService>()),
            NullLogger<ReminderSchedulerService>.Instance, new ConfigurationBuilder().Build());
        await service.CheckAndSendReminders(CancellationToken.None);
        reads.Clear();

        switch (change)
        {
            case "create": db.Habits.Add(habit); break;
            case "update": habit.Update(UpdateParams(habit, "Edited reminder", true)).IsSuccess.Should().BeTrue(); break;
            case "delete": habit.SoftDelete(); break;
            case "restore": habit.Restore(); break;
            case "complete": habit.AdvanceDueDate(today).IsSuccess.Should().BeTrue(); break;
            case "toggle": habit.Update(UpdateParams(habit, habit.Title, true)).IsSuccess.Should().BeTrue(); break;
        }
        await db.SaveChangesAsync();
        reads.Clear();

        await service.CheckAndSendReminders(CancellationToken.None);

        reads.Commands.Should().Contain(c => c.Contains("FROM \"Habits\"", StringComparison.Ordinal)
            && c.Contains("\"Title\"", StringComparison.Ordinal));
        if (change is "create" or "restore" or "toggle")
            (await db.SentReminders.CountAsync(r => r.HabitId == habit.Id)).Should().Be(1);
    }

    private static HabitUpdateParams UpdateParams(Habit habit, string title, bool enabled) =>
        new(title, habit.Description, habit.FrequencyUnit, habit.FrequencyQuantity,
            habit.Days.ToList(), habit.IsBadHabit, habit.DueDate,
            DueTime: habit.DueTime, ReminderEnabled: enabled);

    [Fact]
    public async Task ReminderTick_UserLanguageChange_RefreshesBeforeReminder()
    {
        var reads = new SchedulerReadInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var clock = new MutableTimeProvider(new DateTimeOffset(2027, 9, 26, 8, 59, 0, TimeSpan.Zero));
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Water", FrequencyUnit.Day, 1,
            new DateOnly(2027, 9, 26), DueTime: new TimeOnly(9, 0), ReminderEnabled: true,
            ReminderTimes: [0])).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var push = Substitute.For<IPushNotificationService>();
        var service = new ReminderSchedulerService(Scope(db, push), NullLogger<ReminderSchedulerService>.Instance,
            new ConfigurationBuilder().Build(), clock);
        await service.CheckAndSendReminders(CancellationToken.None);

        user.SetLanguage("pt-BR");
        await db.SaveChangesAsync();
        clock.Set(new DateTimeOffset(2027, 9, 26, 9, 0, 0, TimeSpan.Zero));
        reads.Clear();
        await service.CheckAndSendReminders(CancellationToken.None);

        reads.Commands.Should().Contain(c => c.Contains("\"Title\"", StringComparison.Ordinal));
        await push.Received(1).SendToUserAsync(user.Id, habit.Title, "Agora", "/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReminderTick_UserTimeZoneChange_RefreshesBeforeReminder()
    {
        var reads = new SchedulerReadInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var clock = new MutableTimeProvider(new DateTimeOffset(2027, 9, 26, 7, 59, 0, TimeSpan.Zero));
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Water", FrequencyUnit.Day, 1,
            new DateOnly(2027, 9, 26), DueTime: new TimeOnly(9, 0), ReminderEnabled: true,
            ReminderTimes: [0])).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var push = Substitute.For<IPushNotificationService>();
        var service = new ReminderSchedulerService(Scope(db, push), NullLogger<ReminderSchedulerService>.Instance,
            new ConfigurationBuilder().Build(), clock);
        await service.CheckAndSendReminders(CancellationToken.None);

        user.SetTimeZone("Etc/GMT-1").IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        clock.Set(new DateTimeOffset(2027, 9, 26, 8, 0, 0, TimeSpan.Zero));
        reads.Clear();
        await service.CheckAndSendReminders(CancellationToken.None);

        reads.Commands.Should().Contain(c => c.Contains("\"Title\"", StringComparison.Ordinal));
        await push.Received(1).SendToUserAsync(user.Id, habit.Title, "Due now", "/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReminderTick_ExchangedEligibleOwners_RefreshesBeforeReminder()
    {
        var reads = new SchedulerReadInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var clock = new MutableTimeProvider(new DateTimeOffset(2027, 9, 26, 8, 59, 0, TimeSpan.Zero));
        var firstUser = User.Create("First", "first@test.com").Value;
        var secondUser = User.Create("Second", "second@test.com").Value;
        var latestUser = User.Create("Latest", "latest@test.com").Value;
        secondUser.Deactivate(DateTime.UtcNow.AddDays(30));
        var date = new DateOnly(2027, 9, 26);
        var firstHabit = Habit.Create(new HabitCreateParams(firstUser.Id, "First reminder", FrequencyUnit.Day, 1,
            date, DueTime: new TimeOnly(9, 0), ReminderEnabled: true, ReminderTimes: [0])).Value;
        var secondHabit = Habit.Create(new HabitCreateParams(secondUser.Id, "Second reminder", FrequencyUnit.Day, 1,
            date, DueTime: new TimeOnly(9, 0), ReminderEnabled: true, ReminderTimes: [0])).Value;
        var latestHabit = Habit.Create(new HabitCreateParams(latestUser.Id, "Later reminder", FrequencyUnit.Day, 1,
            date, DueTime: new TimeOnly(12, 0), ReminderEnabled: true, ReminderTimes: [0])).Value;
        db.Users.AddRange(firstUser, secondUser, latestUser);
        db.Habits.AddRange(firstHabit, secondHabit, latestHabit);
        await db.SaveChangesAsync();
        await db.Habits.Where(h => h.Id == latestHabit.Id).ExecuteUpdateAsync(
            setters => setters.SetProperty(h => h.UpdatedAtUtc, DateTime.UtcNow.AddDays(1)));
        var push = Substitute.For<IPushNotificationService>();
        var service = new ReminderSchedulerService(Scope(db, push), NullLogger<ReminderSchedulerService>.Instance,
            new ConfigurationBuilder().Build(), clock);
        await service.CheckAndSendReminders(CancellationToken.None);

        firstUser.Deactivate(DateTime.UtcNow.AddDays(30));
        secondUser.CancelDeactivation();
        await db.SaveChangesAsync();
        clock.Set(new DateTimeOffset(2027, 9, 26, 9, 0, 0, TimeSpan.Zero));
        reads.Clear();
        await service.CheckAndSendReminders(CancellationToken.None);

        reads.Commands.Should().Contain(c => c.Contains("\"Title\"", StringComparison.Ordinal));
        await push.Received(1).SendToUserAsync(secondUser.Id, secondHabit.Title, "Due now", "/",
            Arg.Any<CancellationToken>());
        await push.DidNotReceive().SendToUserAsync(firstUser.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReminderTick_EditedReminderTime_SendsOnNextTick()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var clock = new MutableTimeProvider(new DateTimeOffset(2027, 9, 26, 8, 59, 0, TimeSpan.Zero));
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Water", FrequencyUnit.Day, 1,
            new DateOnly(2027, 9, 26), ReminderEnabled: true,
            ScheduledReminders: [new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(10, 0))])).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var push = Substitute.For<IPushNotificationService>();
        var service = new ReminderSchedulerService(Scope(db, push), NullLogger<ReminderSchedulerService>.Instance,
            new ConfigurationBuilder().Build(), clock);
        await service.CheckAndSendReminders(CancellationToken.None);

        habit.Update(new HabitUpdateParams(habit.Title, habit.Description, habit.FrequencyUnit,
            habit.FrequencyQuantity, habit.Days.ToList(), habit.IsBadHabit, habit.DueDate,
            ReminderEnabled: true,
            ScheduledReminders: [new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0))]))
            .IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        clock.Set(new DateTimeOffset(2027, 9, 26, 9, 0, 0, TimeSpan.Zero));
        await service.CheckAndSendReminders(CancellationToken.None);

        (await db.SentReminders.CountAsync(r => r.HabitId == habit.Id)).Should().Be(1);
        await push.Received(1).SendToUserAsync(user.Id, habit.Title, "Due today", "/",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReminderTick_NewLog_RefreshesBeforeReminder()
    {
        var reads = new SchedulerReadInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var clock = new MutableTimeProvider(new DateTimeOffset(2027, 9, 26, 8, 59, 0, TimeSpan.Zero));
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Water", FrequencyUnit.Day, 1,
            new DateOnly(2027, 9, 26), DueTime: new TimeOnly(9, 0), ReminderEnabled: true,
            ReminderTimes: [0])).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        var push = Substitute.For<IPushNotificationService>();
        var service = new ReminderSchedulerService(Scope(db, push), NullLogger<ReminderSchedulerService>.Instance,
            new ConfigurationBuilder().Build(), clock);
        await service.CheckAndSendReminders(CancellationToken.None);

        var log = habit.Log(new DateOnly(2027, 9, 26)).Value;
        db.HabitLogs.Add(log);
        await db.SaveChangesAsync();
        clock.Set(new DateTimeOffset(2027, 9, 26, 9, 0, 0, TimeSpan.Zero));
        reads.Clear();
        await service.CheckAndSendReminders(CancellationToken.None);

        reads.Commands.Should().Contain(c => c.Contains("FROM \"HabitLogs\"", StringComparison.Ordinal));
        (await db.SentReminders.CountAsync()).Should().Be(0);
        await push.DidNotReceive().SendToUserAsync(Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReminderTick_TwoInstances_OnlyOneSends()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var clock = new MutableTimeProvider(new DateTimeOffset(2027, 9, 26, 8, 59, 0, TimeSpan.Zero));
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Water", FrequencyUnit.Day, 1,
            new DateOnly(2027, 9, 26), ReminderEnabled: true,
            ScheduledReminders: [new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(9, 0))])).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        await db.SaveChangesAsync();

        await using var firstDb = factory.CreateContext();
        await using var secondDb = factory.CreateContext();
        var firstPush = Substitute.For<IPushNotificationService>();
        var secondPush = Substitute.For<IPushNotificationService>();
        var first = new ReminderSchedulerService(Scope(firstDb, firstPush),
            NullLogger<ReminderSchedulerService>.Instance, new ConfigurationBuilder().Build(), clock);
        var second = new ReminderSchedulerService(Scope(secondDb, secondPush),
            NullLogger<ReminderSchedulerService>.Instance, new ConfigurationBuilder().Build(), clock);
        await first.CheckAndSendReminders(CancellationToken.None);
        await second.CheckAndSendReminders(CancellationToken.None);

        clock.Set(new DateTimeOffset(2027, 9, 26, 9, 0, 0, TimeSpan.Zero));
        await first.CheckAndSendReminders(CancellationToken.None);
        await second.CheckAndSendReminders(CancellationToken.None);

        (await db.SentReminders.CountAsync(r => r.HabitId == habit.Id)).Should().Be(1);
        await firstPush.Received(1).SendToUserAsync(user.Id, habit.Title, "Due today", "/",
            Arg.Any<CancellationToken>());
        await secondPush.DidNotReceive().SendToUserAsync(Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReminderTick_UnchangedCandidates_ReloadAfterOneHour()
    {
        var reads = new SchedulerReadInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var clock = new MutableTimeProvider(new DateTimeOffset(2027, 9, 26, 8, 0, 0, TimeSpan.Zero));
        var user = User.Create("Alex", "alex@test.com").Value;
        db.Users.Add(user);
        db.Habits.Add(Habit.Create(new HabitCreateParams(user.Id, "Water", FrequencyUnit.Day, 1,
            new DateOnly(2027, 9, 26), ReminderEnabled: true,
            ScheduledReminders: [new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(12, 0))])).Value);
        await db.SaveChangesAsync();
        var service = new ReminderSchedulerService(Scope(db, Substitute.For<IPushNotificationService>()),
            NullLogger<ReminderSchedulerService>.Instance, new ConfigurationBuilder().Build(), clock);
        await service.CheckAndSendReminders(CancellationToken.None);

        clock.Set(new DateTimeOffset(2027, 9, 26, 9, 0, 0, TimeSpan.Zero));
        reads.Clear();
        await service.CheckAndSendReminders(CancellationToken.None);

        reads.Commands.Should().Contain(c => c.Contains("FROM \"Habits\"", StringComparison.Ordinal)
            && c.Contains("\"Title\"", StringComparison.Ordinal));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Set(DateTimeOffset now) => _now = now;
    }

    [Fact]
    public async Task SlipAlertTick_ProjectedReads_PreservePatternNotification()
    {
        var reads = new SchedulerReadInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var nowUtc = DateTime.UtcNow;
        var offset = 12 - nowUtc.Hour;
        var zone = offset == 0 ? "UTC" : offset > 0 ? $"Etc/GMT-{offset}" : $"Etc/GMT+{-offset}";
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc,
            TimeZoneInfo.FindSystemTimeZoneById(zone)));
        var user = User.Create("Alex", "alex@test.com").Value;
        user.SetTimeZone(zone);
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Doom scrolling", FrequencyUnit.Day, 1,
            localToday, IsBadHabit: true, SlipAlertEnabled: true)).Value;
        for (var i = 0; i < 4; i++)
        {
            var log = habit.Log(localToday.AddDays(-7 * i)).Value;
            typeof(HabitLog).GetProperty(nameof(HabitLog.CreatedAtUtc))!.SetValue(log, nowUtc.AddDays(-7 * i));
        }
        db.Users.Add(user);
        db.Habits.Add(habit);
        db.HabitLogs.AddRange(habit.Logs);
        await db.SaveChangesAsync();
        reads.Clear();

        var push = Substitute.For<IPushNotificationService>();
        var message = Substitute.For<ISlipAlertMessageService>();
        message.GenerateMessageAsync(Arg.Any<string>(), Arg.Any<DayOfWeek>(), Arg.Any<int?>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<(string Title, string Body)>(("Alert", "Body"))));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["BackgroundServices:SlipAlertIntervalMinutes"] = "1440" }).Build();
        var service = new SlipAlertSchedulerService(Scope(db, push, message),
            NullLogger<SlipAlertSchedulerService>.Instance, config);
        await service.CheckAndSendAlerts(CancellationToken.None);

        await push.Received(1).SendToUserAsync(user.Id, "Alert", "Body", "/", Arg.Any<CancellationToken>());
        reads.AssertNarrowHabitAndUserReads();
        reads.Commands.Where(c => c.Contains("HabitLogs", StringComparison.Ordinal))
            .Should().OnlyContain(c => !c.Contains("Note", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckinTick_ProjectedReads_SendOnlyToOffTrackProUser()
    {
        var reads = new SchedulerReadInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(reads);
        var db = factory.Context;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var pro = User.Create("Alex", "alex@test.com").Value;
        pro.SetProactiveAstraEnabled(true);
        var free = User.Create("Sam", "sam@test.com").Value;
        free.SetProactiveAstraEnabled(true);
        free.StartTrial(DateTime.UtcNow.AddDays(-1));
        db.Users.AddRange(pro, free);
        db.Habits.Add(Habit.Create(new HabitCreateParams(pro.Id, "Meditate", FrequencyUnit.Day, 1, today)).Value);
        db.Habits.Add(Habit.Create(new HabitCreateParams(free.Id, "Walk", FrequencyUnit.Day, 1, today)).Value);
        await db.SaveChangesAsync();
        reads.Clear();

        var push = Substitute.For<IPushNotificationService>();
        var message = Substitute.For<IProactiveCheckinMessageService>();
        message.GenerateMessageAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success<(string Title, string Body)>(("Check in", "Body"))));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BackgroundServices:ProactiveCheckinHour"] = "0",
            ["BackgroundServices:ProactiveCheckinIntervalMinutes"] = "1440"
        }).Build();
        var service = new ProactiveCheckinSchedulerService(Scope(db, push, message),
            NullLogger<ProactiveCheckinSchedulerService>.Instance, config);
        await service.CheckAndSendCheckins(CancellationToken.None);

        await push.Received(1).SendToUserAsync(pro.Id, "Check in", "Body", "/chat", Arg.Any<CancellationToken>());
        await push.DidNotReceive().SendToUserAsync(free.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        reads.AssertNarrowHabitAndUserReads();
    }

    private static IServiceScopeFactory Scope(Orbit.Infrastructure.Persistence.OrbitDbContext db,
        IPushNotificationService push, object? message = null)
    {
        var services = new ServiceCollection().AddSingleton(db).AddSingleton(push);
        if (message is ISlipAlertMessageService slip) services.AddSingleton(slip);
        if (message is IProactiveCheckinMessageService checkin) services.AddSingleton(checkin);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class SchedulerReadInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public void Clear() => Commands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public void AssertNarrowHabitAndUserReads()
        {
            Commands.Where(c => c.Contains("FROM \"Habits\"", StringComparison.Ordinal))
                .Should().NotBeEmpty().And.OnlyContain(c => !c.Contains("Description", StringComparison.Ordinal)
                    && !c.Contains("ChecklistItems", StringComparison.Ordinal));
            Commands.Where(c => c.Contains("FROM \"Users\"", StringComparison.Ordinal))
                .Should().NotBeEmpty().And.OnlyContain(c => !c.Contains("Email", StringComparison.Ordinal)
                    && !c.Contains("GoogleAccessToken", StringComparison.Ordinal));
        }
    }
}
