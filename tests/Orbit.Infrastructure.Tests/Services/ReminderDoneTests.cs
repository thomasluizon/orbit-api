using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        log.LoggedOnLocalDate.Should().Be(await _dates.GetUserTodayAsync(habit.UserId));
        log.Value.Should().Be(1);
        log.ReminderAtUtc.Should().Be(_clock.GetUtcNow().UtcDateTime.AddMinutes(-30));
        await _mediator.Received(1).Send(Arg.Is<LogHabitCommand>(c => c.HabitId == habit.Id && !c.ToggleExisting), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HabitSchedule.Flexible, false)]
    [InlineData(HabitSchedule.Flexible, true)]
    [InlineData(HabitSchedule.General, false)]
    [InlineData(HabitSchedule.General, true)]
    public async Task AppDoseThenDone_RefusesLikeReplayAndKeepsOneLog(HabitSchedule schedule, bool withReminder)
    {
        var (handler, habit, token) = await Seed(schedule);
        var reminder = await _factory.Context.SentReminders.SingleAsync();
        var app = await _mediator.Send(new LogHabitCommand(habit.UserId, habit.Id, ToggleExisting: false,
            TakenAtUtc: _clock.Now.UtcDateTime, ReminderAtUtc: withReminder ? reminder.ReminderAtUtc : null));
        app.IsSuccess.Should().BeTrue();

        var done = await handler.Handle(new(token), default);
        var replay = await handler.Handle(new(token), default);

        done.IsFailure.Should().BeTrue();
        replay.IsFailure.Should().BeTrue();
        done.ErrorCode.Should().Be(replay.ErrorCode).And.Be("INVALID_REMINDER_ACTION");
        done.Error.Should().Be(replay.Error);
        (await _factory.Context.HabitLogs.SingleAsync()).Id.Should().Be(app.Value.LogId);
        _factory.Context.ChangeTracker.Clear();
        (await _factory.Context.SentReminders.SingleAsync()).ConsumedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task AppDoseWithReminder_RetiresOnlyTheMatchingParentBeforeDone()
    {
        var (_, habit, _) = await Seed();
        var db = _factory.Context;
        var reminder = await db.SentReminders.SingleAsync();
        var otherHabit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, habit.UserId, "Other",
            FrequencyUnit.Day, 1, habit.StartDate, ReminderEnabled: true, RepeatReminder: new(480))).Value;
        db.Habits.Add(otherHabit);
        var otherInstant = SentReminder.CreateRepeat(habit.Id, reminder.Date, reminder.ReminderAtUtc!.Value.AddMinutes(-1),
            reminder.ExpiresAtUtc!.Value, reminder.DoseAnchorAtUtc!.Value);
        var otherHabitReminder = SentReminder.CreateRepeat(otherHabit.Id, reminder.Date, reminder.ReminderAtUtc.Value,
            reminder.ExpiresAtUtc.Value, reminder.DoseAnchorAtUtc.Value);
        var followUp = SentReminder.CreateRepeat(habit.Id, reminder.Date, reminder.ReminderAtUtc.Value.AddMinutes(15),
            reminder.ExpiresAtUtc.Value, reminder.DoseAnchorAtUtc.Value, reminder.Id);
        db.SentReminders.AddRange(otherInstant, otherHabitReminder, followUp);
        await db.SaveChangesAsync();

        (await _mediator.Send(new LogHabitCommand(habit.UserId, habit.Id, ToggleExisting: false,
            ReminderAtUtc: reminder.ReminderAtUtc))).IsSuccess.Should().BeTrue();

        db.ChangeTracker.Clear();
        var sent = await db.SentReminders.ToListAsync();
        sent.Single(r => r.Id == reminder.Id).ConsumedAtUtc.Should().NotBeNull();
        sent.Where(r => r.Id != reminder.Id).Should().OnlyContain(r => r.ConsumedAtUtc == null);
    }

    [Theory]
    [InlineData(HabitSchedule.Flexible, false)]
    [InlineData(HabitSchedule.Flexible, true)]
    [InlineData(HabitSchedule.General, false)]
    [InlineData(HabitSchedule.General, true)]
    public async Task DoneThenAppDose_KeepsTheIntentionalExtraDose(HabitSchedule schedule, bool withReminder)
    {
        var (handler, habit, token) = await Seed(schedule);
        var done = await handler.Handle(new(token), default);
        done.IsSuccess.Should().BeTrue();
        var reminder = await _factory.Context.SentReminders.SingleAsync();

        var app = await _mediator.Send(new LogHabitCommand(habit.UserId, habit.Id, ToggleExisting: false,
            ReminderAtUtc: withReminder ? reminder.ReminderAtUtc : null));

        app.IsSuccess.Should().BeTrue();
        app.Value.LogId.Should().NotBe(done.Value.LogId);
        (await _factory.Context.HabitLogs.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(HabitSchedule.Flexible, -1)]
    [InlineData(HabitSchedule.Flexible, 0)]
    [InlineData(HabitSchedule.General, -1)]
    [InlineData(HabitSchedule.General, 0)]
    public async Task AppDoseAtReminderBoundary_OnlyEarlierDoseAllowsDone(HabitSchedule schedule, int minutesBefore)
    {
        var (handler, habit, token) = await Seed(schedule);
        var reminder = await _factory.Context.SentReminders.SingleAsync();
        (await _mediator.Send(new LogHabitCommand(habit.UserId, habit.Id, ToggleExisting: false,
            TakenAtUtc: reminder.ReminderAtUtc!.Value.AddMinutes(minutesBefore)))).IsSuccess.Should().BeTrue();

        var done = await handler.Handle(new(token), default);

        done.IsSuccess.Should().Be(minutesBefore < 0);
        (await _factory.Context.HabitLogs.CountAsync()).Should().Be(minutesBefore < 0 ? 2 : 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackfilledAppDose_DoesNotRetireOutstandingReminder(bool legacy)
    {
        var (handler, habit, token) = await Seed();
        if (legacy)
        {
            _factory.Context.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id,
                habit.StartDate.AddDays(1), 1, 0, _clock.Now.UtcDateTime));
            await _factory.Context.SaveChangesAsync();
        }
        else
        {
            (await _mediator.Send(new LogHabitCommand(habit.UserId, habit.Id,
                Date: habit.StartDate.AddDays(1), ToggleExisting: false))).IsSuccess.Should().BeTrue();
        }

        (await handler.Handle(new(token), default)).IsSuccess.Should().BeTrue();
        (await _factory.Context.HabitLogs.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task NewerAppLogWithEarlierDose_DoesNotHideTheLatestDose()
    {
        var (handler, habit, token) = await Seed();
        var reminder = await _factory.Context.SentReminders.SingleAsync();
        (await _mediator.Send(new LogHabitCommand(habit.UserId, habit.Id, ToggleExisting: false,
            TakenAtUtc: _clock.Now.UtcDateTime))).IsSuccess.Should().BeTrue();
        (await _mediator.Send(new LogHabitCommand(habit.UserId, habit.Id, ToggleExisting: false,
            TakenAtUtc: reminder.ReminderAtUtc!.Value.AddMinutes(-1)))).IsSuccess.Should().BeTrue();

        (await handler.Handle(new(token), default)).IsFailure.Should().BeTrue();
        (await _factory.Context.HabitLogs.CountAsync()).Should().Be(2);
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

    [Fact]
    public async Task NextReminder_DoneAndDoseCorrectionMoveTheAuthoritativeReadback()
    {
        var (done, habit, token) = await Seed(HabitSchedule.General);
        var db = _factory.Context;
        var nowAtUtc = _clock.Now.UtcDateTime;
        habit.Update(new HabitUpdateParams(habit.Schedule, habit.Title, habit.Description, habit.FrequencyUnit,
            habit.FrequencyQuantity, [], false, habit.NextDueDate,
            RepeatReminder: new(480, StartAtUtc: nowAtUtc.AddMinutes(-30).AddHours(-8)))).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        var reader = new RepeatReminderReader(db, _dates, _clock);
        var detail = new Orbit.Application.Habits.Queries.GetHabitByIdQueryHandler(new GenericRepository<Habit>(db),
            new GenericRepository<HabitLog>(db), _dates, reader);
        async Task<DateTime?> ReadNext() => (await detail.Handle(new(habit.UserId, habit.Id), default)).Value.NextReminderAtUtc;
        (await ReadNext()).Should().Be(nowAtUtc);

        var completed = await done.Handle(new(token), default);
        completed.IsSuccess.Should().BeTrue();
        (await ReadNext()).Should().Be(nowAtUtc.AddHours(8));
        var correction = new CorrectHabitDoseCommandHandler(new GenericRepository<Habit>(db),
            new UnitOfWork(db, new DatabaseConnectionSettings()), _dates, _cache);
        (await correction.Handle(new(habit.UserId, habit.Id, completed.Value.LogId, nowAtUtc.AddMinutes(-10)), default))
            .IsSuccess.Should().BeTrue();
        (await ReadNext()).Should().Be(nowAtUtc.AddHours(8).AddMinutes(-10));
        _clock.Now = new DateTimeOffset(nowAtUtc.AddHours(8).AddMinutes(-10));
        using var services = new ServiceCollection().AddSingleton(db)
            .AddSingleton(Substitute.For<IPushNotificationService>())
            .AddSingleton<IReminderActionTokenService>(_tokens).BuildServiceProvider();
        var scheduler = new ReminderSchedulerService(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReminderSchedulerService>.Instance, new ConfigurationBuilder().Build(), _clock);
        await scheduler.CheckAndSendReminders(default);
        (await db.SentReminders.OrderByDescending(r => r.ReminderAtUtc).FirstAsync()).ReminderAtUtc
            .Should().Be(_clock.Now.UtcDateTime);
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

    private async Task<(CompleteReminderCommandHandler Handler, Habit Habit, string Token)> Seed(
        HabitSchedule schedule = HabitSchedule.Flexible)
    {
        var db = _factory.Context;
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var user = User.Create("Alex", "alex@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(schedule, user.Id, "Drink water",
            schedule == HabitSchedule.General ? null : FrequencyUnit.Day, schedule == HabitSchedule.General ? null : 1,
            today.AddDays(-2), EndDate: schedule == HabitSchedule.General ? null : today,
            ReminderEnabled: true, RepeatReminder: new(480))).Value;
        var reminderAtUtc = _clock.Now.UtcDateTime.AddMinutes(-30);
        var reminder = SentReminder.CreateRepeat(habit.Id, today, reminderAtUtc,
            reminderAtUtc.AddHours(8), reminderAtUtc.AddHours(-8));
        db.Users.Add(user);
        db.Habits.Add(habit);
        db.SentReminders.Add(reminder);
        await db.SaveChangesAsync();
        _dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(today);
        _dates.GetUserTimeZoneAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        var unit = new UnitOfWork(db, new DatabaseConnectionSettings());
        var streaks = Substitute.For<IUserStreakService>();
        var gamification = Substitute.For<IGamificationService>();
        var goals = new GoalCompletionService(new GenericRepository<Goal>(db), gamification, unit, _dates);
        var habits = new GenericRepository<Habit>(db);
        var logHandler = new LogHabitCommandHandler(new(habits, new GenericRepository<HabitLog>(db), new GenericRepository<User>(db),
                new GenericRepository<SentReminder>(db)),
            new(_dates, streaks, gamification, goals, _mediator, Substitute.For<IPayGateService>()),
            unit, _cache, NullLogger<LogHabitCommandHandler>.Instance);
        _mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => logHandler.Handle(call.Arg<LogHabitCommand>(), call.Arg<CancellationToken>()));
        var handler = new CompleteReminderCommandHandler(_tokens, habits, new GenericRepository<SentReminder>(db),
            new GenericRepository<HabitLog>(db), unit, _dates, _mediator, _clock);
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
