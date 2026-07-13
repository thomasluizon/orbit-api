using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetStreakHistoryQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<StreakFreeze> _freezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IFeatureFlagService _featureFlags = Substitute.For<IFeatureFlagService>();
    private readonly GetStreakHistoryQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public GetStreakHistoryQueryHandlerTests()
    {
        _handler = new GetStreakHistoryQueryHandler(_userRepo, _habitRepo, _habitLogRepo, _freezeRepo, _featureFlags);
        _featureFlags.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        _freezeRepo.FindAsync(Arg.Any<Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>());
    }

    private static User CreateProUser()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.SetStripeSubscription("sub", DateTime.UtcNow.AddYears(1));
        return user;
    }

    private static User CreateFreeUser()
    {
        var user = User.Create("Test", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    private static void BackdateCreation(Habit habit, DateOnly date)
    {
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    private static Habit CreateDailyHabit(DateOnly start)
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Daily", FrequencyUnit.Day, 1, DueDate: start)).Value;
        BackdateCreation(habit, start);
        return habit;
    }

    private static Habit CreateWeekdayHabit(DateOnly start)
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Work", FrequencyUnit.Day, 1,
            Days: new List<DayOfWeek>
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
            },
            DueDate: start)).Value;
        BackdateCreation(habit, start);
        return habit;
    }

    private static void Log(Habit habit, params DateOnly[] dates)
    {
        foreach (var date in dates)
            habit.Log(date, advanceDueDate: false);
    }

    private void ArrangeHabits(params Habit[] habits)
    {
        var list = habits.ToList();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(list);
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(list.SelectMany(h => h.Logs.Where(l => l.Value > 0)).ToList());
    }

    private void ArrangeFreezes(params StreakFreeze[] freezes)
    {
        _freezeRepo.FindAsync(Arg.Any<Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(freezes.ToList());
    }

    [Fact]
    public async Task Handle_ProUser_IncrementsStreakForConsecutiveScheduledDays()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateProUser());
        var habit = CreateDailyHabit(Today.AddDays(-2));
        Log(habit, Today.AddDays(-2), Today.AddDays(-1), Today);
        ArrangeHabits(habit);

        var query = new GetStreakHistoryQuery(UserId, Today.AddDays(-2), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Points.Should().HaveCount(3);
        result.Value.Points.Select(p => p.Streak).Should().Equal(1, 2, 3);
        result.Value.Points[2].Date.Should().Be(Today);
    }

    [Fact]
    public async Task Handle_HabitLogReadFilter_ExcludesLogsFromHabitsTheUserDoesNotOwn()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateProUser());
        var ownedHabit = CreateDailyHabit(Today.AddDays(-2));
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { ownedHabit });

        Expression<Func<HabitLog, bool>>? readFilter = null;
        _habitLogRepo.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                readFilter = call.Arg<Expression<Func<HabitLog, bool>>>();
                return (IReadOnlyList<HabitLog>)new List<HabitLog>();
            });

        await _handler.Handle(new GetStreakHistoryQuery(UserId, Today.AddDays(-2), Today), CancellationToken.None);

        readFilter.Should().NotBeNull();
        var matches = readFilter!.Compile();
        matches(HabitLog.Create(ownedHabit.Id, Today, 1)).Should().BeTrue("the user's own habit log is in scope");
        matches(HabitLog.Create(Guid.NewGuid(), Today, 1)).Should().BeFalse("a log for a habit the user does not own must be excluded");
    }

    [Fact]
    public async Task Handle_FreeUser_ReturnsPayGateFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateFreeUser());

        var query = new GetStreakHistoryQuery(UserId, Today.AddDays(-2), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Domain.Common.Result.PayGateErrorCode);
    }

    [Fact]
    public async Task Handle_WeekdayOnlyHabit_DoesNotResetStreakOnWeekendOffDays()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateProUser());

        var monday = new DateOnly(2026, 3, 16);
        var nextMonday = new DateOnly(2026, 3, 23);
        var habit = CreateWeekdayHabit(monday);
        Log(habit,
            new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 17), new DateOnly(2026, 3, 18),
            new DateOnly(2026, 3, 19), new DateOnly(2026, 3, 20), nextMonday);
        ArrangeHabits(habit);

        var query = new GetStreakHistoryQuery(UserId, monday, nextMonday);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var byDate = result.Value.Points.ToDictionary(p => p.Date, p => p.Streak);

        byDate[new DateOnly(2026, 3, 20)].Should().Be(5);
        byDate[new DateOnly(2026, 3, 21)].Should().Be(5);
        byDate[new DateOnly(2026, 3, 22)].Should().Be(5);
        byDate[nextMonday].Should().Be(6);
        result.Value.Points.Select(p => p.Streak).Should().NotContain(0);
    }

    [Fact]
    public async Task Handle_MissedScheduledDay_BreaksStreak()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateProUser());

        var start = new DateOnly(2026, 3, 16);
        var habit = CreateDailyHabit(start);
        Log(habit,
            new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 17),
            new DateOnly(2026, 3, 19), new DateOnly(2026, 3, 20));
        ArrangeHabits(habit);

        var query = new GetStreakHistoryQuery(UserId, start, Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var byDate = result.Value.Points.ToDictionary(p => p.Date, p => p.Streak);

        byDate[new DateOnly(2026, 3, 17)].Should().Be(2);
        byDate[new DateOnly(2026, 3, 18)].Should().Be(2);
        byDate[new DateOnly(2026, 3, 19)].Should().Be(1);
        byDate[new DateOnly(2026, 3, 20)].Should().Be(2);
    }

    [Fact]
    public async Task Handle_FreezeBridgesMissedScheduledDay_PreservesStreak()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateProUser());

        var start = new DateOnly(2026, 3, 16);
        var habit = CreateDailyHabit(start);
        Log(habit,
            new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 17), new DateOnly(2026, 3, 18),
            new DateOnly(2026, 3, 20));
        ArrangeHabits(habit);
        ArrangeFreezes(StreakFreeze.Create(UserId, new DateOnly(2026, 3, 19)));

        var query = new GetStreakHistoryQuery(UserId, start, Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var byDate = result.Value.Points.ToDictionary(p => p.Date, p => p.Streak);

        byDate[new DateOnly(2026, 3, 18)].Should().Be(3);
        byDate[new DateOnly(2026, 3, 19)].Should().Be(3);
        byDate[new DateOnly(2026, 3, 20)].Should().Be(4);
        result.Value.Points.Select(p => p.Streak).Should().NotContain(0);
    }

    [Fact]
    public async Task Handle_MixedSchedule_FinalPointEqualsCanonicalCurrentStreak()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var dailyStart = new DateOnly(2026, 3, 9);
        var weekdayStart = new DateOnly(2026, 3, 9);
        var daily = CreateDailyHabit(dailyStart);
        for (var i = 0; i <= (Today.DayNumber - dailyStart.DayNumber); i++)
            Log(daily, dailyStart.AddDays(i));

        var weekday = CreateWeekdayHabit(weekdayStart);
        Log(weekday,
            new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 11),
            new DateOnly(2026, 3, 12), new DateOnly(2026, 3, 13),
            new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 17), new DateOnly(2026, 3, 18),
            new DateOnly(2026, 3, 19), new DateOnly(2026, 3, 20));
        ArrangeHabits(daily, weekday);

        var canonicalStreak = await ComputeCanonicalCurrentStreakAsync(user, daily, weekday);

        var query = new GetStreakHistoryQuery(UserId, Today.AddDays(-6), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var finalPoint = result.Value.Points[^1];
        finalPoint.Date.Should().Be(Today);
        finalPoint.Streak.Should().Be(canonicalStreak);
        finalPoint.Streak.Should().Be(user.CurrentStreak);
    }

    private static async Task<int> ComputeCanonicalCurrentStreakAsync(User user, params Habit[] habits)
    {
        var userRepo = Substitute.For<IGenericRepository<User>>();
        var habitRepo = Substitute.For<IGenericRepository<Habit>>();
        var habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
        var freezeRepo = Substitute.For<IGenericRepository<StreakFreeze>>();
        var userDateService = Substitute.For<IUserDateService>();
        var feedEmitter = Substitute.For<IFriendFeedEventEmitter>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var featureFlagService = Substitute.For<IFeatureFlagService>();
        featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habits.ToList());
        habitLogRepo.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habits.SelectMany(h => h.Logs.Where(l => l.Value > 0)).ToList());
        freezeRepo.FindAsync(Arg.Any<Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze>());

        var service = new UserStreakService(
            new UserStreakRepositories(userRepo, habitRepo, habitLogRepo, freezeRepo), userDateService, feedEmitter,
            unitOfWork, featureFlagService, NullLogger<UserStreakService>.Instance);
        var state = await service.RecalculateAsync(UserId, CancellationToken.None);
        return state!.CurrentStreak;
    }
}
