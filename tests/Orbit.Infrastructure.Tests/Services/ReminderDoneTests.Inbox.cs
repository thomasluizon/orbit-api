using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

public sealed partial class ReminderDoneTests
{
    [Theory]
    [InlineData(HabitSchedule.Recurring, false)]
    [InlineData(HabitSchedule.Flexible, false)]
    [InlineData(HabitSchedule.General, false)]
    [InlineData(HabitSchedule.Recurring, true)]
    [InlineData(HabitSchedule.Flexible, true)]
    [InlineData(HabitSchedule.General, true)]
    public async Task Skip_MarksOnlyTheMatchingDailyReminderRead(HabitSchedule schedule, bool bulk)
    {
        var (_, habit, _) = await Seed(schedule);
        var db = _factory.Context;
        var today = await _dates.GetUserTodayAsync(habit.UserId);
        var history = Notification.Create(habit.UserId, habit.Title, "History", "/", habit.Id,
            HabitReminderInbox.Key(habit.Id, today.AddDays(-1)));
        db.Notifications.Add(history);
        await db.SaveChangesAsync();
        var habits = new GenericRepository<Habit>(db);
        var logs = new GenericRepository<HabitLog>(db);
        var notifications = new GenericRepository<Notification>(db);
        var unit = new UnitOfWork(db, new DatabaseConnectionSettings());
        if (bulk)
        {
            var handler = new BulkSkipHabitsCommandHandler(habits, logs, _dates, unit, _cache, notifications);
            var result = await handler.Handle(new(habit.UserId, [new(habit.Id)]), default);
            result.Value.Results.Single().Status.Should().Be(BulkItemStatus.Success);
        }
        else
        {
            var handler = new SkipHabitCommandHandler(new(habits, logs, new GenericRepository<HabitSkipUndo>(db), notifications),
                _dates, Substitute.For<IGoalCompletionService>(), unit, _cache);
            (await handler.Handle(new(habit.UserId, habit.Id), default)).IsSuccess.Should().BeTrue();
        }

        db.ChangeTracker.Clear();
        var rows = await db.Notifications.ToListAsync();
        rows.Single(n => n.Id == history.Id).IsRead.Should().BeFalse();
        rows.Single(n => n.Id != history.Id).IsRead.Should().BeTrue();
    }

    [Theory]
    [InlineData(HabitSchedule.Recurring)]
    [InlineData(HabitSchedule.Flexible)]
    [InlineData(HabitSchedule.General)]
    public async Task BulkCompletion_MarksTheDailyReminderRead(HabitSchedule schedule)
    {
        var (_, habit, _) = await Seed(schedule);
        var db = _factory.Context;
        var unit = new UnitOfWork(db, new DatabaseConnectionSettings());
        var gamification = Substitute.For<IGamificationService>();
        var goals = new GoalCompletionService(new GenericRepository<Goal>(db), gamification, unit, _dates);
        var handler = new BulkLogHabitsCommandHandler(new GenericRepository<Habit>(db), new GenericRepository<HabitLog>(db),
            new(_dates, Substitute.For<IUserStreakService>(), gamification, new GenericRepository<Notification>(db)),
            goals, unit, _cache, NullLogger<BulkLogHabitsCommandHandler>.Instance);

        var result = await handler.Handle(new(habit.UserId, [new(habit.Id)]), default);

        result.Value.Results.Single().Status.Should().Be(BulkItemStatus.Success);
        db.ChangeTracker.Clear();
        (await db.Notifications.SingleAsync()).IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task Completion_LeavesUntypedRowsUnread()
    {
        var (_, habit, _) = await Seed();
        var db = _factory.Context;
        var today = await _dates.GetUserTodayAsync(habit.UserId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        _dates.GetUserTimeZoneAsync(habit.UserId, Arg.Any<CancellationToken>()).Returns(zone);
        var legacy = Notification.Create(habit.UserId, habit.Title, "Legacy", "/", habit.Id);
        db.Notifications.Add(legacy);
        db.Entry(legacy).Property(n => n.CreatedAtUtc).CurrentValue = today.AddDays(1).ToDateTime(new(2, 30), DateTimeKind.Utc);
        await db.SaveChangesAsync();

        (await _mediator.Send(new LogHabitCommand(habit.UserId, habit.Id, ToggleExisting: false)))
            .IsSuccess.Should().BeTrue();

        db.ChangeTracker.Clear();
        var rows = await db.Notifications.ToListAsync();
        rows.Single(n => n.Id == legacy.Id).IsRead.Should().BeFalse();
        rows.Single(n => n.Id != legacy.Id).IsRead.Should().BeTrue();
    }
}
