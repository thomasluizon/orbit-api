using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Gamification;

public class AchievementProgressServiceTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly AchievementProgressService _service;
    private readonly List<(Func<HabitLog, bool> Predicate, IReadOnlyList<HabitMetricLog> Rows)> _streakReads = [];

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 7, 17);

    public AchievementProgressServiceTests()
    {
        _service = new AchievementProgressService(
            _habitRepo, _habitLogRepo, _goalRepo, _userDateService);
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
        var startDate = Today.AddDays(-Math.Max(1200, streakDays));
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit", FrequencyUnit.Day, 1, startDate)).Value;
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, startDate.ToDateTime(TimeOnly.MinValue));
        for (var day = 0; day < streakDays; day++)
        {
            var logDate = Today.AddDays(-day);
            var log = habit.Log(logDate, advanceDueDate: false).Value;
            typeof(HabitLog).GetProperty(nameof(HabitLog.CreatedAtUtc))!
                .SetValue(log, logDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        }
        return habit;
    }

    /// <summary>
    /// Builds a daily BAD habit with NO logs and a backdated creation, so its abstinence "streak"
    /// (consecutive non-logged expected days) runs long — the value that must NOT count toward streak progress.
    /// </summary>
    private static Habit CreateBadHabitWithAbstinenceStreak()
    {
        var habit = Habit.Create(
            new HabitCreateParams(
                UserId, "Bad Habit", FrequencyUnit.Day, 1, Today.AddDays(-400), IsBadHabit: true)).Value;
        return habit;
    }

    private void StubHabits(params Habit[] habits)
    {
        _habitRepo.ProjectAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<HabitScheduleSnapshot>>>(),
            Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<IQueryable<Habit>, IQueryable<HabitScheduleSnapshot>>>(1)(
                habits.AsQueryable()).ToList());
        var logs = habits.SelectMany(habit => habit.Logs).Where(log => !log.IsDeleted).ToList();
        _habitLogRepo.ProjectAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<Func<IQueryable<HabitLog>, IQueryable<HabitMetricLog>>>(),
            Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.ArgAt<Expression<Func<HabitLog, bool>>>(0).Compile();
                var rows = call.ArgAt<Func<IQueryable<HabitLog>, IQueryable<HabitMetricLog>>>(1)(
                    logs.Where(predicate).AsQueryable()).ToList();
                _streakReads.Add((predicate, rows));
                return rows;
            });
        _habitLogRepo.ProjectAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<Func<IQueryable<HabitLog>, IQueryable<DateTime>>>(),
            Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<IQueryable<HabitLog>, IQueryable<DateTime>>>(1)(
                logs.AsQueryable()).ToList());
    }

    private void StubCounts()
    {
        _habitLogRepo.CountAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>()).Returns(42);
        _goalRepo.CountAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>()).Returns(7, 3);
    }

    [Fact]
    public async Task LoadAsync_WithHabits_MapsEveryCountToItsMetric()
    {
        var user = CreateUser(streak: 9);
        var habit = CreateHabitWithStreak(5);
        var oldLog = habit.Log(Today.AddDays(-65), advanceDueDate: false).Value;
        typeof(HabitLog).GetProperty(nameof(HabitLog.CreatedAtUtc))!
            .SetValue(oldLog, oldLog.Date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        StubHabits(habit);
        StubCounts();

        var metrics = await _service.LoadAsync(user, new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(5);
        _streakReads.Should().ContainSingle();
        _streakReads[0].Predicate(oldLog).Should().BeFalse();
        metrics.TotalCompletions.Should().Be(42);
        metrics.GoalsCreated.Should().Be(7);
        metrics.GoalsCompleted.Should().Be(3);
        metrics.EarlyLogs.Should().Be(0);
        metrics.NightLogs.Should().Be(0);
        await _habitLogRepo.Received(1).ProjectAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<Func<IQueryable<HabitLog>, IQueryable<HabitMetricLog>>>(),
            Arg.Any<CancellationToken>());

        var repositoryQueryCount = _habitRepo.ReceivedCalls().Count()
            + _habitLogRepo.ReceivedCalls().Count()
            + _goalRepo.ReceivedCalls().Count();
        repositoryQueryCount.Should().Be(6);
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
        await _habitLogRepo.DidNotReceive().ProjectAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<Func<IQueryable<HabitLog>, IQueryable<DateTime>>>(),
            Arg.Any<CancellationToken>());
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
        await _habitLogRepo.DidNotReceive().ProjectAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<Func<IQueryable<HabitLog>, IQueryable<HabitMetricLog>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadAsync_HighUnionStreakButLowPerHabitStreaks_ReturnsMaxPerHabitStreakNotUnion()
    {
        var user = CreateUser(streak: 30);
        StubHabits(CreateHabitWithStreak(3), CreateHabitWithStreak(1));
        StubCounts();

        var metrics = await _service.LoadAsync(user, new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(3);
    }

    [Fact]
    public async Task LoadAsync_BadHabitAbstinenceStreak_ExcludedFromStreakProgress()
    {
        var user = CreateUser(streak: 30);
        var badHabit = CreateBadHabitWithAbstinenceStreak();
        StubHabits(CreateHabitWithStreak(2), badHabit);
        StubCounts();

        var badHabitRawStreak = HabitMetricsCalculator.Calculate(badHabit, Today, 1).CurrentStreak;

        var metrics = await _service.LoadAsync(user, new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(2);
        badHabitRawStreak.Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task LoadAsync_RepeatedDeletedAndSkippedLogs_PreservesFlexibleStreak()
    {
        var user = CreateUser();
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Flexible", FrequencyUnit.Day, 2,
            DueDate: Today.AddDays(-2), IsFlexible: true)).Value;
        var start = Today.AddDays(-2);
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        habit.Log(Today.AddDays(-1), advanceDueDate: false);
        habit.Log(Today.AddDays(-1), advanceDueDate: false);
        habit.Log(Today, advanceDueDate: false);
        habit.Unlog(Today);
        habit.Log(Today, advanceDueDate: false);
        habit.Log(Today, advanceDueDate: false);
        habit.SkipFlexible(Today);
        StubHabits(habit);
        StubCounts();

        var metrics = await _service.LoadAsync(user, new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_LegacyFlexibleSkip_PreservesStreakBeforeMovingDueDate()
    {
        var user = CreateUser();
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Legacy flexible", FrequencyUnit.Day, 1,
            DueDate: Today, IsFlexible: true)).Value;
        var start = Today.AddDays(-3);
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        typeof(Habit).GetProperty(nameof(Habit.ScheduledStartDate))!
            .SetValue(habit, null);
        habit.SkipFlexible(start);
        habit.Log(Today.AddDays(-2), advanceDueDate: false);
        habit.Log(Today.AddDays(-1), advanceDueDate: false);
        habit.Log(Today, advanceDueDate: false);
        StubHabits(habit);
        StubCounts();

        var metrics = await _service.LoadAsync(user, new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(3);
    }
    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(1099)]
    [InlineData(1100)]
    [InlineData(1101)]
    public async Task LoadAsync_StreakCrossesWindowBoundaries_MatchesFullWindow(int streakDays)
    {
        var habit = CreateHabitWithStreak(streakDays);
        StubHabits(habit);
        StubCounts();

        var metrics = await _service.LoadAsync(CreateUser(), new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(FullWindowStreak(habit));
        metrics.CurrentStreak.Should().Be(Math.Min(streakDays, 1100));
        _streakReads.Should().HaveCount(streakDays <= 64 ? 1
            : streakDays <= 128 ? 2 : streakDays <= 256 ? 3
            : streakDays <= 512 ? 4 : streakDays <= 1024 ? 5 : 6);
        _streakReads.SelectMany(read => read.Rows).Select(log => log.Date).Should().OnlyHaveUniqueItems();
        _streakReads.SelectMany(read => read.Rows).Should().OnlyContain(log => log.Date >= Today.AddDays(-1100));
    }

    [Fact]
    public async Task LoadAsync_AllStreakAchievementsEarned_SkipsStreakRead()
    {
        var habit = CreateHabitWithStreak(1000);
        StubHabits(habit);
        StubCounts();
        var earned = AchievementDefinitions.All
            .Where(definition => definition.Metric == ProgressMetric.CurrentStreak)
            .Select(definition => definition.Id).ToHashSet();

        var metrics = await _service.LoadAsync(CreateUser(), earned, CancellationToken.None);

        metrics.CurrentStreak.Should().Be(0);
        _streakReads.Should().BeEmpty();
        foreach (var definition in AchievementDefinitions.All.Where(definition => earned.Contains(definition.Id)))
            AchievementProgressCalculator.Compute(definition, metrics, true)
                .Should().Be((definition.ProgressTarget, definition.ProgressTarget));
        metrics.TotalCompletions.Should().Be(42);
    }

    [Fact]
    public async Task LoadAsync_OnlyImmortalUnEarned_StillReachesThreshold()
    {
        StubHabits(CreateHabitWithStreak(1000));
        StubCounts();
        var earned = AchievementDefinitions.All
            .Where(definition => definition.Metric == ProgressMetric.CurrentStreak
                && definition.Id != AchievementDefinitions.StreakImmortal)
            .Select(definition => definition.Id).ToHashSet();

        var metrics = await _service.LoadAsync(CreateUser(), earned, CancellationToken.None);

        metrics.CurrentStreak.Should().Be(1000);
    }

    [Fact]
    public async Task LoadAsync_WidensOnlyContinuingGoodHabits()
    {
        var shortHabit = CreateHabitWithStreak(2);
        shortHabit.Log(Today.AddDays(-100), advanceDueDate: false);
        var longHabit = CreateHabitWithStreak(300);
        var badHabit = CreateBadHabitWithAbstinenceStreak();
        badHabit.Log(Today, advanceDueDate: false);
        StubHabits(shortHabit, longHabit, badHabit);
        StubCounts();

        var metrics = await _service.LoadAsync(CreateUser(), new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(300);
        _streakReads.Should().HaveCount(4);
        _streakReads.Skip(1).SelectMany(read => read.Rows).Should().OnlyContain(log => log.HabitId == longHabit.Id);
        _streakReads.SelectMany(read => read.Rows).Should().NotContain(log => log.HabitId == badHabit.Id);
    }

    [Fact]
    public async Task LoadAsync_OnlyBadHabits_SkipsStreakRead()
    {
        StubHabits(CreateBadHabitWithAbstinenceStreak());
        StubCounts();

        var metrics = await _service.LoadAsync(CreateUser(), new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(0);
        _streakReads.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FrequencyUnit.Day, false, 7)]
    [InlineData(FrequencyUnit.Week, false, 1)]
    [InlineData(FrequencyUnit.Month, false, 1)]
    [InlineData(FrequencyUnit.Year, false, 1)]
    [InlineData(FrequencyUnit.Week, true, 2)]
    [InlineData(FrequencyUnit.Month, true, 2)]
    [InlineData(FrequencyUnit.Year, true, 2)]
    public async Task LoadAsync_SparseOrFlexibleSchedule_MatchesFullWindow(
        FrequencyUnit unit, bool flexible, int quantity)
    {
        var start = Today.AddDays(-1200);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Scheduled", unit, quantity, start, IsFlexible: flexible)).Value;
        if (flexible)
        {
            for (var date = start; date <= Today; date = date.AddDays(1))
                habit.Log(date, advanceDueDate: false);
        }
        else
        {
            for (var date = Today; date >= start; date = unit switch
            {
                FrequencyUnit.Day => date.AddDays(-quantity),
                FrequencyUnit.Week => date.AddDays(-7 * quantity),
                FrequencyUnit.Month => date.AddMonths(-quantity),
                _ => date.AddYears(-quantity)
            })
                habit.Log(date, advanceDueDate: false);
        }
        StubHabits(habit);
        StubCounts();

        var metrics = await _service.LoadAsync(CreateUser(), new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(FullWindowStreak(habit));
        _streakReads.Should().HaveCountGreaterThan(1);
        _streakReads.SelectMany(read => read.Rows).Select(log => (log.Date, log.Value)).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_LegacyAnchorNeedsOlderHistory_MatchesFullWindow(bool flexible)
    {
        var habit = CreateHabitWithStreak(300);
        typeof(Habit).GetProperty(nameof(Habit.ScheduledStartDate))!.SetValue(habit, null);
        typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, Today.AddDays(-299).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        typeof(Habit).GetProperty(nameof(Habit.DueDate))!.SetValue(habit, Today.AddDays(1));
        typeof(Habit).GetProperty(nameof(Habit.IsFlexible))!.SetValue(habit, flexible);
        StubHabits(habit);
        StubCounts();

        var metrics = await _service.LoadAsync(CreateUser(), new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(300);
        metrics.CurrentStreak.Should().Be(FullWindowStreak(habit));
    }

    [Theory]
    [InlineData("America/Sao_Paulo")]
    [InlineData("Pacific/Kiritimati")]
    public async Task LoadAsync_TodayNotCompleted_UsesUserTodayAcrossBoundary(string timeZone)
    {
        var habit = CreateHabitWithStreak(129);
        habit.Unlog(Today);
        var user = CreateUser();
        user.SetTimeZone(timeZone).IsSuccess.Should().BeTrue();
        StubHabits(habit);
        StubCounts();

        var metrics = await _service.LoadAsync(user, new HashSet<string>(), CancellationToken.None);

        metrics.CurrentStreak.Should().Be(128);
        await _userDateService.Received(1).GetUserTodayAsync(user.Id, CancellationToken.None);
    }

    private static int FullWindowStreak(Habit habit) => HabitMetricsCalculator.CalculateProjected(
        habit, habit.Logs.Where(log => !log.IsDeleted && log.Date >= Today.AddDays(-1100))
            .Select(log => new HabitMetricLog(log.HabitId, log.Date, log.Value, false)).ToList(),
        Today, 1, TimeZoneInfo.Utc).CurrentStreak;

}
