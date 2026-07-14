using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
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
    private readonly IFriendFeedEventEmitter _feedEmitter = Substitute.For<IFriendFeedEventEmitter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();

    private readonly UserStreakService _sut;

    private static readonly Guid UserId = Guid.NewGuid();

    public UserStreakServiceTests()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        _sut = new UserStreakService(
            new UserStreakRepositories(
                _userRepository,
                _habitRepository,
                _habitLogRepository,
                _streakFreezeRepository),
            _userDateService,
            _feedEmitter,
            _unitOfWork,
            _featureFlagService,
            NullLogger<UserStreakService>.Instance);
    }

    private void EnableGamificationFreeTier() =>
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { FeatureFlagKeys.GamificationFreeTier });

    /// <summary>
    /// Grants banked streak freezes directly, bypassing the milestone-award path so a recalc scenario
    /// can start from a chosen accumulated-freeze count.
    /// </summary>
    private static void SetBankedFreezes(User user, int count) =>
        typeof(User).GetProperty(nameof(User.StreakFreezesAccumulated))!.SetValue(user, count);

    private static Habit CreateDailyHabitLoggedOn(IEnumerable<DateOnly> completions, DateOnly createdOn)
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Run", FrequencyUnit.Day, 1, DueDate: createdOn)).Value;
        BackdateCreation(habit, createdOn);
        foreach (var date in completions)
            habit.Log(date, advanceDueDate: false);
        return habit;
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

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

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

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

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

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(3);
        result.LongestStreak.Should().Be(3);
        user.CurrentStreak.Should().Be(3);
    }

    [Fact]
    public async Task RecalculateAsync_WeekdayOnlyHabit_StreakContinuesAcrossWeekend()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var weekdays = new List<DayOfWeek>
        {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday
        };
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Work", FrequencyUnit.Day, 1,
            Days: weekdays,
            DueDate: new DateOnly(2026, 4, 6))).Value;
        BackdateCreation(habit, new DateOnly(2026, 4, 6));

        habit.Log(new DateOnly(2026, 4, 6), advanceDueDate: false);        habit.Log(new DateOnly(2026, 4, 7), advanceDueDate: false);        habit.Log(new DateOnly(2026, 4, 8), advanceDueDate: false);        habit.Log(new DateOnly(2026, 4, 9), advanceDueDate: false);        habit.Log(new DateOnly(2026, 4, 10), advanceDueDate: false);        habit.Log(new DateOnly(2026, 4, 13), advanceDueDate: false);
        SetupUser(user, new DateOnly(2026, 4, 13));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

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

        habit.Log(new DateOnly(2026, 4, 1), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 2), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 4), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 5), advanceDueDate: false);

        SetupUser(user, new DateOnly(2026, 4, 5));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(2);        result.LongestStreak.Should().Be(2);
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

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

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
        habit.Log(new DateOnly(2026, 4, 5), advanceDueDate: false);

        SetupUser(user, new DateOnly(2026, 4, 5));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze> { StreakFreeze.Create(UserId, new DateOnly(2026, 4, 4)) });

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(4);
    }

    [Fact]
    public async Task RecalculateAsync_OnlyBadHabits_DoesNotIncreaseStreak()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var bad = Habit.Create(new HabitCreateParams(
            UserId, "Smoke", FrequencyUnit.Day, 1,
            IsBadHabit: true,
            DueDate: new DateOnly(2026, 4, 1))).Value;

        bad.Log(new DateOnly(2026, 4, 1), advanceDueDate: false);
        bad.Log(new DateOnly(2026, 4, 2), advanceDueDate: false);
        bad.Log(new DateOnly(2026, 4, 3), advanceDueDate: false);

        SetupUser(user, new DateOnly(2026, 4, 3));
        SetupHabits(new List<Habit> { bad });
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(0);
    }

    [Fact]
    public async Task RecalculateAsync_RecentMissedDay_WithBankedFreeze_AppliesFreezeAndPreservesStreak()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetStreakState(5, 5, new DateOnly(2026, 4, 5));
        SetBankedFreezes(user, 1);
        EnableGamificationFreeTier();

        var completions = new[]
        {
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3),
            new DateOnly(2026, 4, 4), new DateOnly(2026, 4, 5)
        };
        var habit = CreateDailyHabitLoggedOn(completions, new DateOnly(2026, 4, 1));

        SetupUser(user, new DateOnly(2026, 4, 7));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(5);
        result.LastActiveDate.Should().Be(new DateOnly(2026, 4, 6));
        user.CurrentStreak.Should().Be(5);
        user.StreakFreezesAccumulated.Should().Be(0);
        await _streakFreezeRepository.Received(1).AddAsync(
            Arg.Is<StreakFreeze>(f => f.UserId == user.Id && f.UsedOnDate == new DateOnly(2026, 4, 6)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecalculateAsync_RunTwice_ConsumesFreezeOnceAndKeepsStreakStable()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetStreakState(5, 5, new DateOnly(2026, 4, 5));
        SetBankedFreezes(user, 1);
        EnableGamificationFreeTier();

        var completions = new[]
        {
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3),
            new DateOnly(2026, 4, 4), new DateOnly(2026, 4, 5)
        };
        var habit = CreateDailyHabitLoggedOn(completions, new DateOnly(2026, 4, 1));

        SetupUser(user, new DateOnly(2026, 4, 7));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze>());

        var first = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        SetupFreezes(new List<StreakFreeze> { StreakFreeze.Create(UserId, new DateOnly(2026, 4, 6)) });

        var second = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        first!.CurrentStreak.Should().Be(5);
        second!.CurrentStreak.Should().Be(5);
        user.StreakFreezesAccumulated.Should().Be(0);
        await _streakFreezeRepository.Received(1).AddAsync(
            Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecalculateAsync_GapTooLargeForOneFreeze_BreaksStreakWithoutConsuming()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetStreakState(4, 4, new DateOnly(2026, 4, 4));
        SetBankedFreezes(user, 1);
        EnableGamificationFreeTier();

        var completions = new[]
        {
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2),
            new DateOnly(2026, 4, 3), new DateOnly(2026, 4, 4)
        };
        var habit = CreateDailyHabitLoggedOn(completions, new DateOnly(2026, 4, 1));

        SetupUser(user, new DateOnly(2026, 4, 7));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        result!.CurrentStreak.Should().Be(0);
        user.StreakFreezesAccumulated.Should().Be(1);
        await _streakFreezeRepository.DidNotReceive().AddAsync(
            Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecalculateAsync_RecentMissedDay_NoBankedFreeze_BreaksStreak()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetStreakState(5, 5, new DateOnly(2026, 4, 5));
        SetBankedFreezes(user, 0);

        var completions = new[]
        {
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3),
            new DateOnly(2026, 4, 4), new DateOnly(2026, 4, 5)
        };
        var habit = CreateDailyHabitLoggedOn(completions, new DateOnly(2026, 4, 1));

        SetupUser(user, new DateOnly(2026, 4, 7));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze>());

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        result!.CurrentStreak.Should().Be(0);
        user.StreakFreezesAccumulated.Should().Be(0);
        await _streakFreezeRepository.DidNotReceive().AddAsync(
            Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecalculateAsync_ConcurrentFreezeInsertConflict_PreservesStreakConsumesOnceAndDoesNotThrow()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetStreakState(5, 5, new DateOnly(2026, 4, 5));
        SetBankedFreezes(user, 1);
        EnableGamificationFreeTier();

        var completions = new[]
        {
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3),
            new DateOnly(2026, 4, 4), new DateOnly(2026, 4, 5)
        };
        var habit = CreateDailyHabitLoggedOn(completions, new DateOnly(2026, 4, 1));

        SetupUser(user, new DateOnly(2026, 4, 7));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze>());

        var uniqueViolation = new DbUpdateException("duplicate freeze", new UniqueViolationDbException());
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(uniqueViolation));

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(5);
        result.LastActiveDate.Should().Be(new DateOnly(2026, 4, 6));
        user.StreakFreezesAccumulated.Should().Be(0);
        await _streakFreezeRepository.Received(1).AddAsync(
            Arg.Is<StreakFreeze>(f => f.UsedOnDate == new DateOnly(2026, 4, 6)),
            Arg.Any<CancellationToken>());
        _unitOfWork.Received(1).DiscardChanges();
    }

    [Fact]
    public async Task RecalculateAsync_MonthlyFreezeCapReached_BreaksStreakWithoutConsuming()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.SetStreakState(5, 5, new DateOnly(2026, 4, 5));
        SetBankedFreezes(user, 1);
        EnableGamificationFreeTier();

        var completions = new[]
        {
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 2), new DateOnly(2026, 4, 3),
            new DateOnly(2026, 4, 4), new DateOnly(2026, 4, 5)
        };
        var habit = CreateDailyHabitLoggedOn(completions, new DateOnly(2026, 4, 1));

        SetupUser(user, new DateOnly(2026, 4, 7));
        SetupHabits(new List<Habit> { habit });
        SetupFreezes(new List<StreakFreeze>
        {
            StreakFreeze.Create(UserId, new DateOnly(2026, 4, 1)),
            StreakFreeze.Create(UserId, new DateOnly(2026, 4, 2)),
            StreakFreeze.Create(UserId, new DateOnly(2026, 4, 3))
        });

        var result = await _sut.RecalculateAsync(UserId, cancellationToken: CancellationToken.None);

        result!.CurrentStreak.Should().Be(0);
        user.StreakFreezesAccumulated.Should().Be(1);
        await _streakFreezeRepository.DidNotReceive().AddAsync(
            Arg.Any<StreakFreeze>(), Arg.Any<CancellationToken>());
    }

    private sealed class UniqueViolationDbException : DbException
    {
        public override string SqlState => "23505";
    }
}
