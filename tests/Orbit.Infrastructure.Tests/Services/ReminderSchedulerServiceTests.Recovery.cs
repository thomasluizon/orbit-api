using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Services;

public partial class ReminderSchedulerServiceTests
{
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
        var push = Substitute.For<IPushNotificationService>();
        push.SendToUserAsync(user.Id, habit.Title, "Due now", "/", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("push unavailable")), Task.CompletedTask);
        var service = CreateService(db, push);
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.AsNoTracking().SingleAsync()).PushSentAtUtc.Should().BeNull();
        await service.CheckAndSendReminders(default);
        (await db.SentReminders.AsNoTracking().SingleAsync()).PushSentAtUtc.Should().NotBeNull();
        await service.CheckAndSendReminders(default);
        await push.Received(2).SendToUserAsync(user.Id, habit.Title, "Due now", "/", Arg.Any<CancellationToken>());
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
        var push = Substitute.For<IPushNotificationService>();
        push.SendToUserAsync(user.Id, habit.Title, "Due now", "/", Arg.Any<CancellationToken>()).Returns(call =>
        {
            stop.Cancel();
            call.Arg<CancellationToken>().IsCancellationRequested.Should().BeFalse();
            call.Arg<CancellationToken>().CanBeCanceled.Should().BeTrue();
            return Task.CompletedTask;
        });
        await CreateService(db, push).CheckAndSendReminders(stop.Token);
        var rows = await db.SentReminders.AsNoTracking().ToListAsync();
        rows.Single(row => row.Id == reminder.Id).PushSentAtUtc.Should().NotBeNull();
        rows.Single(row => row.Id == oldReminder.Id).PushSentAtUtc.Should().BeNull();
        await push.Received(1).SendToUserAsync(user.Id, habit.Title, "Due now", "/", Arg.Any<CancellationToken>());
        await push.DidNotReceive().SendToUserAsync(user.Id, habit.Title, "Too old", "/", Arg.Any<CancellationToken>());
    }
}
