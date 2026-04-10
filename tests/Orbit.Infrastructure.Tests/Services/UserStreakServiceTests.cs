using FluentAssertions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class UserStreakServiceTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepository = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<StreakFreeze> _streakFreezeRepository = Substitute.For<IGenericRepository<StreakFreeze>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();

    private readonly UserStreakService _sut;

    private static readonly Guid UserId = Guid.NewGuid();

    public UserStreakServiceTests()
    {
        _sut = new UserStreakService(
            _userRepository,
            _habitRepository,
            _habitLogRepository,
            _streakFreezeRepository,
            _userDateService);
    }

    private void SetupUser(User user, DateOnly today)
    {
        _userRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
    }

    private void SetupHabits(List<Habit> habits)
    {
        _habitRepository.FindAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(habits);
        _habitLogRepository.FindAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.SelectMany(h => h.Logs).ToList());
    }

    /// <summary>
    /// Backdates CreatedAtUtc so historical schedule calculation includes dates before today.
    /// </summary>
    private static void BackdateCreation(Habit habit, DateOnly date)
    {
        var prop = typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!;
        prop.SetValue(habit, date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    private void SetupFreezes(List<StreakFreeze> freezes)
    {
        _streakFreezeRepository.FindAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<StreakFreeze, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(freezes);
    }

    [Fact]
    public async Task RecalculateAsync_UserNotFound_ReturnsNull()
    {
        _userRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task RecalculateAsync_NoHabits_ResetsStreakState()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetStreakState(5, 7, new DateOnly(2026, 4, 3));

        SetupUser(user, new DateOnly(2026, 4, 10));
        SetupHabits(new List<Habit>());
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(0);
        result.LongestStreak.Should().Be(0);
        result.LastActiveDate.Should().BeNull();
        user.CurrentStreak.Should().Be(0);
        user.LongestStreak.Should().Be(0);
    }

    [Fact]
    public async Task RecalculateAsync_DailyHabit_CompletionsAndFreeze_PreservesAndContinuesStreak()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(UserId, "Run", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;
        BackdateCreation(habit, new DateOnly(2026, 4, 1));

        habit.Log(new DateOnly(2026, 4, 1), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 2), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 4), advanceDueDate: false);

        SetupUser(user, new DateOnly(2026, 4, 4));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze> { StreakFreeze.Create(UserId, new DateOnly(2026, 4, 3)) });

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(3);
        result.LongestStreak.Should().Be(3);
        user.CurrentStreak.Should().Be(3);
    }

    [Fact]
    public async Task RecalculateAsync_WeekdayOnlyHabit_StreakContinuesAcrossWeekend()
    {
        // Mon-Fri schedule, user logs every weekday, misses Sat & Sun.
        // Streak should NOT reset on the weekend because Sat/Sun aren't expected.
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var weekdays = new List<DayOfWeek>
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
        };
        // Start: Monday 2026-04-06
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Work", FrequencyUnit.Day, 1,
            Days: weekdays,
            DueDate: new DateOnly(2026, 4, 6))).Value;
        BackdateCreation(habit, new DateOnly(2026, 4, 6));

        // Log Mon-Fri (week 1) and Mon (week 2) -- weekend untouched
        habit.Log(new DateOnly(2026, 4, 6), advanceDueDate: false);  // Mon
        habit.Log(new DateOnly(2026, 4, 7), advanceDueDate: false);  // Tue
        habit.Log(new DateOnly(2026, 4, 8), advanceDueDate: false);  // Wed
        habit.Log(new DateOnly(2026, 4, 9), advanceDueDate: false);  // Thu
        habit.Log(new DateOnly(2026, 4, 10), advanceDueDate: false); // Fri
        habit.Log(new DateOnly(2026, 4, 13), advanceDueDate: false); // Mon

        SetupUser(user, new DateOnly(2026, 4, 13));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(6);
        result.LongestStreak.Should().Be(6);
    }

    [Fact]
    public async Task RecalculateAsync_MissedExpectedDay_StopsStreak()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(UserId, "Run", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;
        BackdateCreation(habit, new DateOnly(2026, 4, 1));

        // Missed April 3 (expected, no log, no freeze)
        habit.Log(new DateOnly(2026, 4, 1), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 2), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 4), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 5), advanceDueDate: false);

        SetupUser(user, new DateOnly(2026, 4, 5));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(2); // April 4-5
        result.LongestStreak.Should().Be(2);
    }

    [Fact]
    public async Task RecalculateAsync_MixedDailyAndWeekdayHabits_RequiresAtLeastOneLogPerExpectedDay()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var daily = Habit.Create(new HabitCreateParams(UserId, "Water", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 6))).Value;
        BackdateCreation(daily, new DateOnly(2026, 4, 6));
        var weekdayHabit = Habit.Create(new HabitCreateParams(
            UserId, "Work", FrequencyUnit.Day, 1,
            Days: new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            DueDate: new DateOnly(2026, 4, 6))).Value;
        BackdateCreation(weekdayHabit, new DateOnly(2026, 4, 6));

        // Log water every day, weekday habit on weekdays only. Covers everything -> streak continuous.
        foreach (var d in Enumerable.Range(0, 7))
            daily.Log(new DateOnly(2026, 4, 6).AddDays(d), advanceDueDate: false);
        weekdayHabit.Log(new DateOnly(2026, 4, 6), advanceDueDate: false);
        weekdayHabit.Log(new DateOnly(2026, 4, 7), advanceDueDate: false);
        weekdayHabit.Log(new DateOnly(2026, 4, 8), advanceDueDate: false);
        weekdayHabit.Log(new DateOnly(2026, 4, 9), advanceDueDate: false);
        weekdayHabit.Log(new DateOnly(2026, 4, 10), advanceDueDate: false);

        SetupUser(user, new DateOnly(2026, 4, 12));
        SetupHabits(new List<Habit> { daily, weekdayHabit });
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(7);
    }

    [Fact]
    public async Task RecalculateAsync_FreezeBridgesMissedExpectedDay()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(UserId, "Run", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;
        BackdateCreation(habit, new DateOnly(2026, 4, 1));

        habit.Log(new DateOnly(2026, 4, 1), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 2), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 3), advanceDueDate: false);
        // Missed April 4, freeze on April 4
        habit.Log(new DateOnly(2026, 4, 5), advanceDueDate: false);

        SetupUser(user, new DateOnly(2026, 4, 5));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze> { StreakFreeze.Create(UserId, new DateOnly(2026, 4, 4)) });

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(4);
    }

    [Fact]
    public async Task RecalculateAsync_NoRecurringHabits_FallsBackToCalendarAdjacency()
    {
        // User only has bad habits -> no contributing recurring habits -> calendar fallback kicks in.
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var bad = Habit.Create(new HabitCreateParams(
            UserId, "Smoke", FrequencyUnit.Day, 1,
            IsBadHabit: true,
            DueDate: new DateOnly(2026, 4, 1))).Value;

        // Also create a flexible habit; still not "contributing". Use its logs for the fallback.
        bad.Log(new DateOnly(2026, 4, 1), advanceDueDate: false);
        bad.Log(new DateOnly(2026, 4, 2), advanceDueDate: false);
        bad.Log(new DateOnly(2026, 4, 3), advanceDueDate: false);

        SetupUser(user, new DateOnly(2026, 4, 3));
        SetupHabits(new List<Habit> { bad });
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(3);
    }
}
