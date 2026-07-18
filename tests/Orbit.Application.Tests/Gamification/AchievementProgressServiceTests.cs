using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Gamification;

public class AchievementProgressServiceTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<Cheer> _cheerRepo = Substitute.For<IGenericRepository<Cheer>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepo = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepo = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly AchievementProgressService _service;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 7, 17);

    public AchievementProgressServiceTests()
    {
        var friendGraphService = new FriendGraphService(_userRepo, _friendshipRepo, _blockedUserRepo);
        _service = new AchievementProgressService(
            _habitRepo, _habitLogRepo, _goalRepo, _cheerRepo, friendGraphService, _userDateService);
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
    }

    private static User CreateUser(int streak = 0)
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetStreakState(streak, streak, Today.AddDays(-1));
        return user;
    }

    private static Habit CreateHabit() =>
        Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, new DateOnly(2026, 1, 1))).Value;

    /// <summary>
    /// Builds a daily habit whose own current streak is exactly <paramref name="streakDays"/>: it backdates
    /// the habit's creation so the streak window has room, then logs the last N consecutive days ending today.
    /// </summary>
    private static Habit CreateHabitWithStreak(int streakDays)
    {
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, Today)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, Today.AddDays(-400).ToDateTime(TimeOnly.MinValue));
        for (var day = 0; day < streakDays; day++)
            habit.Log(Today.AddDays(-day), advanceDueDate: false);
        return habit;
    }

    private void StubHabits(params Habit[] habits) =>
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.ToList());

    private void StubCounts()
    {
        _habitLogRepo.CountAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>()).Returns(42);
        _goalRepo.CountAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>()).Returns(7, 3);
        _friendshipRepo.CountAsync(Arg.Any<Expression<Func<Friendship, bool>>>(), Arg.Any<CancellationToken>()).Returns(6);
        _cheerRepo.CountAsync(Arg.Any<Expression<Func<Cheer, bool>>>(), Arg.Any<CancellationToken>()).Returns(15);
    }

    [Fact]
    public async Task LoadAsync_WithHabits_MapsEveryCountToItsMetric()
    {
        var user = CreateUser(streak: 9);
        StubHabits(CreateHabitWithStreak(5));
        _habitLogRepo.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog>());
        StubCounts();

        var metrics = await _service.LoadAsync(user, new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(5);
        metrics.TotalCompletions.Should().Be(42);
        metrics.GoalsCreated.Should().Be(7);
        metrics.GoalsCompleted.Should().Be(3);
        metrics.FriendsCount.Should().Be(6);
        metrics.CheersSent.Should().Be(15);
        metrics.EarlyLogs.Should().Be(0);
        metrics.NightLogs.Should().Be(0);
        await _habitLogRepo.Received(1).FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_BothTimeOfDayAchievementsEarned_SkipsTheLogScan()
    {
        var user = CreateUser();
        StubHabits(CreateHabit());
        StubCounts();
        var earned = new HashSet<string> { AchievementDefinitions.EarlyBird, AchievementDefinitions.NightOwl };

        var metrics = await _service.LoadAsync(user, earned, CancellationToken.None);

        metrics.EarlyLogs.Should().Be(0);
        metrics.NightLogs.Should().Be(0);
        await _habitLogRepo.DidNotReceive().FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_NoHabits_SkipsAllLogQueries()
    {
        var user = CreateUser();
        StubHabits();
        StubCounts();

        var metrics = await _service.LoadAsync(user, new HashSet<string>(), CancellationToken.None);

        metrics.TotalCompletions.Should().Be(0);
        await _habitLogRepo.DidNotReceive().CountAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>());
        await _habitLogRepo.DidNotReceive().FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_HighUnionStreakButLowPerHabitStreaks_ReturnsMaxPerHabitStreakNotUnion()
    {
        var user = CreateUser(streak: 30);
        StubHabits(CreateHabitWithStreak(3), CreateHabitWithStreak(1));
        _habitLogRepo.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<HabitLog>());
        StubCounts();

        var metrics = await _service.LoadAsync(user, new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(3);
    }
}
