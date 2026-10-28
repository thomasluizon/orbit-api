using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ReminderDoneTests : IDisposable
{
    private readonly SqliteOrbitDbContextFactory _factory = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly FixedClock _clock = new(TimeProvider.System.GetUtcNow());
    private readonly ReminderActionTokenService _tokens = new(Options.Create(new JwtSettings
    {
        SecretKey = new string('s', 48),
        Issuer = "orbit",
        Audience = "orbit"
    }));

    [Fact]
    public async Task TwoDevices_ConsumeOneTokenThroughTheRealLogCommandExactlyOnce()
    {
        var (handler, habit, token) = await Seed();
        (await handler.Handle(new(token), default)).IsSuccess.Should().BeTrue();
        (await handler.Handle(new(token), default)).IsFailure.Should().BeTrue();
        var log = await _factory.Context.HabitLogs.SingleAsync();
        log.TakenAtUtc.Should().Be(_clock.GetUtcNow().UtcDateTime);
        log.Value.Should().Be(1);
        log.ReminderAtUtc.Should().Be(_clock.GetUtcNow().UtcDateTime.AddMinutes(-30));
        await _mediator.Received(1).Send(Arg.Is<LogHabitCommand>(c => c.HabitId == habit.Id && !c.ToggleExisting), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CorrectDose_PreservesTheReminderInstantAndInvalidatesTheClockSnapshot()
    {
        var (done, habit, token) = await Seed();
        var completed = await done.Handle(new(token), default);
        completed.IsSuccess.Should().BeTrue();
        var db = _factory.Context;
        var before = habit.ReminderProbeVersion;
        var correctedAtUtc = _clock.Now.UtcDateTime.AddMinutes(-10);
        var correction = new CorrectHabitDoseCommandHandler(new GenericRepository<Habit>(db),
            new UnitOfWork(db, new DatabaseConnectionSettings()), _dates, _cache);
        (await correction.Handle(new(habit.UserId, habit.Id, completed.Value.LogId, correctedAtUtc), default))
            .IsSuccess.Should().BeTrue();
        db.ChangeTracker.Clear();
        var persisted = await db.Habits.SingleAsync();
        persisted.ReminderProbeVersion.Should().BeGreaterThan(before);
        var logs = new Orbit.Application.Habits.Queries.GetHabitLogsQueryHandler(new GenericRepository<Habit>(db),
            new HabitLogReader(db), _dates);
        var response = await logs.Handle(new(habit.UserId, habit.Id), default);
        response.Value.Single().TakenAtUtc.Should().Be(correctedAtUtc);
        response.Value.Single().ReminderAtUtc.Should().Be(_clock.Now.UtcDateTime.AddMinutes(-30));
        (await correction.Handle(new(Guid.NewGuid(), habit.Id, completed.Value.LogId, correctedAtUtc.AddMinutes(-1)), default))
            .IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("other-account")]
    [InlineData("deleted")]
    [InlineData("ended")]
    [InlineData("completed")]
    [InlineData("tampered")]
    public async Task InvalidAction_IsRefusedWithoutLogging(string reason)
    {
        var (handler, habit, token) = await Seed();
        Guid? caller = null;
        if (reason == "expired") _clock.Now = _clock.Now.AddHours(8);
        if (reason == "other-account") caller = Guid.NewGuid();
        if (reason == "deleted")
        {
            habit.SoftDelete();
            await _factory.Context.SaveChangesAsync();
        }
        if (reason == "completed")
        {
            habit.AdvanceDueDate(habit.EndDate!.Value);
            await _factory.Context.SaveChangesAsync();
        }
        if (reason == "ended")
            _dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(habit.EndDate!.Value.AddDays(1));
        if (reason == "tampered") token = token[..^2] + "xx";
        (await handler.Handle(new(token, caller), default)).IsFailure.Should().BeTrue();
        (await _factory.Context.HabitLogs.CountAsync()).Should().Be(0);
        await _mediator.DidNotReceive().Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>());
    }

    private async Task<(CompleteReminderCommandHandler Handler, Habit Habit, string Token)> Seed()
    {
        var db = _factory.Context;
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, user.Id, "Drink water", FrequencyUnit.Day, 1,
            today, EndDate: today, ReminderEnabled: true, RepeatReminder: new(480))).Value;
        var reminderAtUtc = _clock.Now.UtcDateTime.AddMinutes(-30);
        var reminder = SentReminder.CreateRepeat(habit.Id, today, reminderAtUtc,
            reminderAtUtc.AddHours(8), reminderAtUtc.AddHours(-8));
        db.Users.Add(user);
        db.Habits.Add(habit);
        db.SentReminders.Add(reminder);
        await db.SaveChangesAsync();
        _dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(today);
        var unit = new UnitOfWork(db, new DatabaseConnectionSettings());
        var streaks = Substitute.For<IUserStreakService>();
        var goals = Substitute.For<IGoalCompletionService>();
        goals.SyncDerivedGoalsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<GoalCompletionUpdate>());
        var habits = new GenericRepository<Habit>(db);
        var logHandler = new LogHabitCommandHandler(new(habits, new GenericRepository<HabitLog>(db), new GenericRepository<User>(db)),
            new(_dates, streaks, Substitute.For<IGamificationService>(), goals, _mediator, Substitute.For<IPayGateService>()),
            unit, _cache, NullLogger<LogHabitCommandHandler>.Instance);
        _mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => logHandler.Handle(call.Arg<LogHabitCommand>(), call.Arg<CancellationToken>()));
        var handler = new CompleteReminderCommandHandler(_tokens, habits, new GenericRepository<SentReminder>(db), unit, _dates, _mediator, _clock);
        var token = _tokens.Sign(new(user.Id, habit.Id, reminder.Id, reminderAtUtc, reminderAtUtc.AddHours(8)));
        return (handler, habit, token);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _factory.Dispose();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
