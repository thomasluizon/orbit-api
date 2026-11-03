using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public partial class ReminderSchedulerServiceTests
{
    [Fact]
    public async Task BatchDeadline_PreservesAcceptedPushesAndRetriesUnreachedRecipients()
    {
        using var db = CreateSqliteDbContext();
        var user = User.Create("User", "deadline-batch@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user.Id, "Task", null, null, UtcToday)).Value;
        var reminders = Enumerable.Range(0, 40).Select(index =>
        {
            var reminder = SentReminder.Create(habit.Id, UtcToday, index);
            reminder.PreparePush(user.Id, habit.Title, $"Reminder {index}", recordedAtUtc: DefaultInstant.UtcDateTime.AddMinutes(-1));
            return reminder;
        }).ToArray();
        db.AddRange(user, habit, PushSubscription.Create(user.Id, "apns-token", "apns", "install-secret").Value);
        db.SentReminders.AddRange(reminders);
        await db.SaveChangesAsync();
        var attempts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var accepted = new System.Collections.Concurrent.ConcurrentBag<string>();
        var first = 0;
        var retry = false;
        var sender = Substitute.For<IApnsPushSender>();
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(async call =>
            {
                var body = call.ArgAt<string>(2);
                attempts.AddOrUpdate(body, 1, (_, count) => count + 1);
                if (!retry && Interlocked.Increment(ref first) > 1)
                    await Task.Delay(TimeSpan.FromSeconds(9), call.Arg<CancellationToken>());
                accepted.Add(body);
                return ApnsDeliveryOutcome.Delivered;
            });
        using var push = new PushNotificationService(db, Options.Create(new VapidSettings()),
            NullLogger<PushNotificationService>.Instance, new HttpClient(), sender, Substitute.For<IWnsPushSender>());
        var service = CreateService(db, push, forwardBatches: false);

        var error = await Record.ExceptionAsync(() => service.CheckAndSendReminders(default));
        if (error is not null) error.Should().BeAssignableTo<OperationCanceledException>();

        accepted.Should().NotBeEmpty().And.HaveCountLessThan(reminders.Length);
        var delivered = accepted.ToArray();
        attempts.Count.Should().BeLessThan(reminders.Length);
        var rows = await db.SentReminders.AsNoTracking().ToListAsync();
        rows.Where(row => row.PushSentAtUtc != null).Select(row => row.PushBody).Should().BeEquivalentTo(delivered);

        retry = true;
        await service.CheckAndSendReminders(default);
        await service.CheckAndSendReminders(default);

        (await db.SentReminders.AsNoTracking().ToListAsync()).Should().OnlyContain(row => row.PushSentAtUtc != null);
        attempts.Keys.Should().BeEquivalentTo(reminders.Select(row => row.PushBody));
        foreach (var body in delivered) attempts[body].Should().Be(1);
        accepted.Should().HaveCount(reminders.Length).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task BatchDelivery_RecordsOnlySuccessfulReminderAndRetriesTheOther()
    {
        using var db = CreateSqliteDbContext();
        var user = User.Create("User", "partial-batch@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user.Id, "Task", null, null, UtcToday)).Value;
        var delivered = SentReminder.Create(habit.Id, UtcToday, 0);
        delivered.PreparePush(user.Id, habit.Title, "Delivered", recordedAtUtc: DefaultInstant.UtcDateTime.AddMinutes(-1));
        var retry = SentReminder.Create(habit.Id, UtcToday, 10);
        retry.PreparePush(user.Id, habit.Title, "Retry", recordedAtUtc: DefaultInstant.UtcDateTime.AddMinutes(-1));
        db.AddRange(user, habit, delivered, retry,
            PushSubscription.Create(user.Id, "apns-token", "apns", "install-secret").Value);
        await db.SaveChangesAsync();
        var sender = Substitute.For<IApnsPushSender>();
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(call => call.ArgAt<string>(2) == "Delivered" ? ApnsDeliveryOutcome.Delivered : ApnsDeliveryOutcome.Retained);
        var push = new PushNotificationService(db, Options.Create(new VapidSettings()),
            NullLogger<PushNotificationService>.Instance, new HttpClient(), sender, Substitute.For<IWnsPushSender>());
        var service = CreateService(db, push, forwardBatches: false);

        await service.CheckAndSendReminders(default);

        var rows = await db.SentReminders.AsNoTracking().ToListAsync();
        rows.Single(row => row.Id == delivered.Id).PushSentAtUtc.Should().NotBeNull();
        rows.Single(row => row.Id == retry.Id).PushSentAtUtc.Should().BeNull();
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), "Retry", Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(ApnsDeliveryOutcome.Delivered);
        await service.CheckAndSendReminders(default);
        await service.CheckAndSendReminders(default);

        (await db.SentReminders.AsNoTracking().ToListAsync()).Should().OnlyContain(row => row.PushSentAtUtc != null);
        await sender.Received(1).SendAsync("apns-token", habit.Title, "Delivered", "/", Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await sender.Received(2).SendAsync("apns-token", habit.Title, "Retry", "/", Arg.Any<CancellationToken>(),
            Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Fact]
    public async Task NoDeviceAcceptsThePush_LeavesDeliveryPending()
    {
        using var db = CreateSqliteDbContext();
        var user = User.Create("User", "unavailable-push@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user.Id, "Task", null, null, UtcToday)).Value;
        var reminder = SentReminder.Create(habit.Id, UtcToday, 0);
        reminder.PreparePush(user.Id, habit.Title, "Due now", recordedAtUtc: DefaultInstant.UtcDateTime.AddMinutes(-1));
        db.AddRange(user, habit, reminder);
        await db.SaveChangesAsync();
        var push = new PushNotificationService(db, Options.Create(new VapidSettings()),
            NullLogger<PushNotificationService>.Instance, new HttpClient(),
            Substitute.For<IApnsPushSender>(), Substitute.For<IWnsPushSender>());
        await CreateService(db, push, forwardBatches: false).CheckAndSendReminders(default);
        (await db.SentReminders.AsNoTracking().SingleAsync()).PushSentAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task MissingPush_IsRetriedOnTheNextTickAndRecordedAfterSuccess()
    {
        using var db = CreateSqliteDbContext();
        var user = User.Create("User", "reminder-retry@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user.Id, "Task", null, null, DueDate: UtcToday)).Value;
        var reminder = SentReminder.Create(habit.Id, UtcToday, 0);
        reminder.PreparePush(user.Id, habit.Title, "Due now", recordedAtUtc: DefaultInstant.UtcDateTime.AddMinutes(-1));
        db.AddRange(user, habit, reminder);
        await db.SaveChangesAsync();
        var push = SuccessfulPush();
        push.TrySendToUserAsync(user.Id, habit.Title, "Due now", "/", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("push unavailable")), Task.FromResult(true));
        var service = CreateService(db, push);
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.AsNoTracking().SingleAsync()).PushSentAtUtc.Should().BeNull();
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.AsNoTracking().SingleAsync()).PushSentAtUtc.Should().NotBeNull();
        await service.CheckAndSendReminders(default);
        await push.Received(2).TrySendToUserAsync(user.Id, habit.Title, "Due now", "/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HostStopDuringPush_DoesNotCancelTheBoundedBatch()
    {
        using var db = CreateSqliteDbContext();
        var user = User.Create("User", "reminder-stop@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.OneTime, user.Id, "Task", null, null, DueDate: UtcToday)).Value;
        var reminder = SentReminder.Create(habit.Id, UtcToday, 0);
        reminder.PreparePush(user.Id, habit.Title, "Due now", recordedAtUtc: DefaultInstant.UtcDateTime.AddMinutes(-1));
        var oldReminder = SentReminder.Create(habit.Id, UtcToday, 10);
        oldReminder.PreparePush(user.Id, habit.Title, "Too old", recordedAtUtc: DefaultInstant.UtcDateTime.AddMinutes(-16));
        db.AddRange(user, habit, reminder, oldReminder);
        await db.SaveChangesAsync();
        using var stop = new CancellationTokenSource();
        var push = SuccessfulPush();
        push.TrySendToUserAsync(user.Id, habit.Title, "Due now", "/", Arg.Any<CancellationToken>()).Returns(call =>
        {
            stop.Cancel();
            call.Arg<CancellationToken>().IsCancellationRequested.Should().BeFalse();
            call.Arg<CancellationToken>().CanBeCanceled.Should().BeTrue();
            return Task.FromResult(true);
        });
        await CreateService(db, push).CheckAndSendReminders(stop.Token);
        var rows = await db.SentReminders.AsNoTracking().ToListAsync();
        rows.Single(row => row.Id == reminder.Id).PushSentAtUtc.Should().NotBeNull();
        rows.Single(row => row.Id == oldReminder.Id).PushSentAtUtc.Should().BeNull();
        await push.Received(1).TrySendToUserAsync(user.Id, habit.Title, "Due now", "/", Arg.Any<CancellationToken>());
        await push.DidNotReceive().TrySendToUserAsync(user.Id, habit.Title, "Too old", "/", Arg.Any<CancellationToken>());
    }
}
