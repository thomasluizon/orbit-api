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

    private readonly UserStreakService _sut;

    private static readonly Guid UserId = Guid.NewGuid();

    public UserStreakServiceTests()
    {
        _sut = new UserStreakService(_userRepository, _habitRepository, _habitLogRepository, _streakFreezeRepository);
    }

    [Fact]
    public async Task RecalculateAsync_CompletionsAndFreeze_PreservesAndContinuesStreak()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(UserId, "Run", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;

        habit.Log(new DateOnly(2026, 4, 1), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 2), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 4), advanceDueDate: false);

        _userRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _habitRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });
        _habitLogRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habit.Logs.ToList());
        _streakFreezeRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze> { StreakFreeze.Create(UserId, new DateOnly(2026, 4, 3)) });

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(3);
        result.LongestStreak.Should().Be(3);
        result.LastActiveDate.Should().Be(new DateOnly(2026, 4, 4));
        user.CurrentStreak.Should().Be(3);
    }

    [Fact]
    public async Task RecalculateAsync_FreezeOneDayAfterLastCompletion_PreservesStreak()
    {
        // Bug scenario: user completed on April 1-3, missed April 4,
        // activated freeze on April 5. The freeze should bridge the 1-day gap.
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(UserId, "Run", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;

        habit.Log(new DateOnly(2026, 4, 1), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 2), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 3), advanceDueDate: false);

        _userRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _habitRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });
        _habitLogRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habit.Logs.ToList());
        _streakFreezeRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze> { StreakFreeze.Create(UserId, new DateOnly(2026, 4, 5)) });

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(3);
        result.LongestStreak.Should().Be(3);
        result.LastActiveDate.Should().Be(new DateOnly(2026, 4, 5));
        user.CurrentStreak.Should().Be(3);
    }

    [Fact]
    public async Task RecalculateAsync_FreezeOneDayAfterLastCompletion_ContinuesWithNextCompletion()
    {
        // User completed on April 1-3, freeze on April 5 (missed April 4),
        // completed on April 6. Streak should be 4 (3 completions + freeze + 1 completion).
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(UserId, "Run", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;

        habit.Log(new DateOnly(2026, 4, 1), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 2), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 3), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 6), advanceDueDate: false);

        _userRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _habitRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });
        _habitLogRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habit.Logs.ToList());
        _streakFreezeRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze> { StreakFreeze.Create(UserId, new DateOnly(2026, 4, 5)) });

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(4);
        result.LongestStreak.Should().Be(4);
        result.LastActiveDate.Should().Be(new DateOnly(2026, 4, 6));
        user.CurrentStreak.Should().Be(4);
    }

    [Fact]
    public async Task RecalculateAsync_GapBeforeFreeze_BreaksStreakInsteadOfRevivingIt()
    {
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(UserId, "Run", FrequencyUnit.Day, 1, DueDate: new DateOnly(2026, 4, 1))).Value;

        habit.Log(new DateOnly(2026, 4, 1), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 2), advanceDueDate: false);
        habit.Log(new DateOnly(2026, 4, 6), advanceDueDate: false);

        _userRepository.FindOneTrackedAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
        _habitRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit });
        _habitLogRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habit.Logs.ToList());
        _streakFreezeRepository.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StreakFreeze> { StreakFreeze.Create(UserId, new DateOnly(2026, 4, 5)) });

        var result = await _sut.RecalculateAsync(UserId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CurrentStreak.Should().Be(1);
        result.LongestStreak.Should().Be(2);
        result.LastActiveDate.Should().Be(new DateOnly(2026, 4, 6));
        user.CurrentStreak.Should().Be(1);
    }
}
