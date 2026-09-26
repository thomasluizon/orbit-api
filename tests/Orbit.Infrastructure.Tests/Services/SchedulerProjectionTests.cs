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
