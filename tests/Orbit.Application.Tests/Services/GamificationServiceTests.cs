using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Npgsql;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Services;

public class GamificationServiceTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepo = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepo = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<Notification> _notificationRepo = Substitute.For<IGenericRepository<Notification>>();
    private readonly IGenericRepository<XpAwardLog> _xpAwardLogRepo = Substitute.For<IGenericRepository<XpAwardLog>>();
    private readonly IPushNotificationService _pushService = Substitute.For<IPushNotificationService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IFriendFeedEventEmitter _feedEmitter = Substitute.For<IFriendFeedEventEmitter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();
    private readonly IFoundingAchievementReader _foundingAchievementReader = Substitute.For<IFoundingAchievementReader>();
    private readonly GamificationService _sut;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public GamificationServiceTests()
    {
        var repos = new GamificationRepositories(
            _userRepo, _habitRepo, _habitLogRepo, _goalRepo, _achievementRepo, _notificationRepo, _xpAwardLogRepo,
            _foundingAchievementReader);
        _sut = new GamificationService(
            repos, new GamificationNotifiers(_pushService, _feedEmitter), _userDateService, new XpAwarder(_xpAwardLogRepo), _unitOfWork,
            _featureFlagService, Substitute.For<ILogger<GamificationService>>());

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<IReadOnlyList<string>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<IReadOnlyList<string>>>>(0)(
                call.ArgAt<CancellationToken>(1)));
    }

    private void EnableFreeTierFlag()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { FeatureFlagKeys.GamificationFreeTier });
    }

    private static User CreateProUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddYears(1));
        return user;
    }

    private static User CreateFreeUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    private static Habit CreateTestHabit(Guid? userId = null, bool isBadHabit = false)
    {
        var habit = Habit.Create(new HabitCreateParams(
            userId ?? UserId, "Test Habit", FrequencyUnit.Day, 1,
            DueDate: Today, IsBadHabit: isBadHabit)).Value;

        typeof(Habit).GetProperty("CreatedAtUtc")!.SetValue(habit, Today.ToDateTime(TimeOnly.MinValue));
        return habit;
    }

    /// <summary>
    /// Creates a daily habit with N consecutive logs ending at Today.
    /// Sets CreatedAtUtc via reflection so HabitMetricsCalculator can
    /// generate expected dates covering the full streak range.
    /// </summary>
    private static Habit CreateDailyHabitWithStreak(int streakDays)
    {
        var startDate = Today.AddDays(-(streakDays - 1));
        var habit = Habit.Create(new HabitCreateParams(UserId, "Daily", FrequencyUnit.Day, 1, DueDate: startDate)).Value;

        typeof(Habit).GetProperty("CreatedAtUtc")!.SetValue(habit, startDate.ToDateTime(TimeOnly.MinValue));

        for (int i = 0; i < streakDays; i++)
            habit.Log(startDate.AddDays(i));
        return habit;
    }

    /// <summary>
    /// Creates a daily habit with N total logs, starting far enough back.
    /// </summary>
    private static Habit CreateHabitWithNLogs(int logCount)
    {
        var startDate = Today.AddDays(-(logCount + 5));
        var habit = Habit.Create(new HabitCreateParams(UserId, "Habit with logs", FrequencyUnit.Day, 1,
            DueDate: startDate)).Value;

        typeof(Habit).GetProperty("CreatedAtUtc")!.SetValue(habit, startDate.ToDateTime(TimeOnly.MinValue));

        for (int i = 0; i < logCount; i++)
            habit.Log(startDate.AddDays(i));

        return habit;
    }

    private void SetupUserLookup(User user)
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);
    }

    private void SetupNoEarnedAchievements()
    {
        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>());
    }

    private void SetupEarnedAchievements(params string[] achievementIds)
    {
        var earned = achievementIds.Select(id => UserAchievement.Create(UserId, id)).ToList();
        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(earned);
    }

    private void SetupHabitWithLogs(Habit habit)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);
    }

    private void SetupUserHabits(params Habit[] habits)
    {
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.ToList());

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.ToList());

        _habitLogRepo.CountAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(habits.Sum(h => h.Logs.Count));
    }

    private void SetupHabitLogs(params HabitLog[] logs)
    {
        _habitLogRepo.FindAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(logs.ToList());
    }

    private void SetupHabitCount(int count)
    {
        _habitRepo.CountAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(count);
    }

    private void SetupGoalCount(int count)
    {
        _goalRepo.CountAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(count);
    }

    private void SetupCompletedGoalCount(int count)
    {
        _goalRepo.CountAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(count);
    }

    [Fact]
    public async Task ProcessHabitCreated_FirstHabit_GrantsFirstOrbit()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        SetupHabitCount(1);

        await _sut.ProcessHabitCreated(UserId);

        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.FirstOrbit),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitCreated_SecondHabit_DoesNotGrantFirstOrbit()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        SetupHabitCount(2);

        await _sut.ProcessHabitCreated(UserId);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.FirstOrbit),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_FirstCompletion_GrantsLiftoff()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit = CreateTestHabit();
        habit.Log(Today);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.Liftoff),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_7DayStreak_GrantsWeekWarrior()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit = CreateDailyHabitWithStreak(7);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.WeekWarrior),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_14DayStreak_GrantsFortnightFocus()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit = CreateDailyHabitWithStreak(14);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.FortnightFocus),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_30DayStreak_GrantsMonthlyMaster()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit = CreateDailyHabitWithStreak(30);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.MonthlyMaster),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_6DayStreak_DoesNotGrantWeekWarrior()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit = CreateDailyHabitWithStreak(6);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.WeekWarrior),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_10Completions_GrantsGettingMomentum()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit = CreateHabitWithNLogs(10);
        SetupUserHabits(habit);

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.GettingMomentum),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_50Completions_GrantsBuildingHabits()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit = CreateHabitWithNLogs(50);
        SetupUserHabits(habit);

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.BuildingHabits),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_100Completions_GrantsDedicated()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit = CreateHabitWithNLogs(100);
        SetupUserHabits(habit);

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.Dedicated),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_9Completions_DoesNotGrantGettingMomentum()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit = CreateHabitWithNLogs(9);
        SetupUserHabits(habit);

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.GettingMomentum),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCreated_FirstGoal_GrantsMissionControl()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        SetupGoalCount(1);

        await _sut.ProcessGoalCreated(UserId);

        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.MissionControl),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCreated_ThirdGoal_GrantsGoalSetter()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        SetupGoalCount(3);

        await _sut.ProcessGoalCreated(UserId);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.GoalSetter),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCompleted_FirstGoal_GrantsGoalCrusherAndXp()
    {
        var user = CreateProUser();
        var initialXp = user.TotalXp;
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        SetupCompletedGoalCount(1);

        await _sut.ProcessGoalCompleted(UserId, GoalId);

        user.TotalXp.Should().BeGreaterThan(initialXp);
        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.GoalCrusher),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCompleted_FiveGoals_GrantsOverachiever()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        SetupCompletedGoalCount(5);

        await _sut.ProcessGoalCompleted(UserId, GoalId);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.Overachiever),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCompleted_TenGoals_GrantsDreamMaker()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        SetupCompletedGoalCount(10);

        await _sut.ProcessGoalCompleted(UserId, GoalId);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.DreamMaker),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitCreated_AlreadyEarnedFirstOrbit_DoesNotGrantAgain()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupEarnedAchievements(AchievementDefinitions.FirstOrbit);

        SetupHabitCount(1);

        await _sut.ProcessHabitCreated(UserId);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_AlreadyEarnedLiftoff_DoesNotGrantAgain()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupEarnedAchievements(AchievementDefinitions.Liftoff, AchievementDefinitions.LegendaryVolume);

        var habit = CreateTestHabit();
        habit.Log(Today);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.Liftoff),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitCreated_FreeUser_DoesNothing()
    {
        var user = CreateFreeUser();
        SetupUserLookup(user);

        await _sut.ProcessHabitCreated(UserId);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitCreated_FreeUserWithFlag_GrantsFirstOrbit()
    {
        var user = CreateFreeUser();
        EnableFreeTierFlag();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();
        SetupHabitCount(1);

        await _sut.ProcessHabitCreated(UserId);

        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.FirstOrbit),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_FreeUser_DoesNothing()
    {
        var user = CreateFreeUser();
        SetupUserLookup(user);

        await _sut.ProcessHabitLogged(UserId, Guid.NewGuid());

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCompleted_FreeUser_DoesNothing()
    {
        var user = CreateFreeUser();
        SetupUserLookup(user);

        await _sut.ProcessGoalCompleted(UserId, GoalId);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCreated_FreeUserWithFlag_GrantsMissionControl()
    {
        var user = CreateFreeUser();
        EnableFreeTierFlag();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();
        SetupGoalCount(1);

        await _sut.ProcessGoalCreated(UserId);

        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.MissionControl),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitCreated_UserNotFound_DoesNothing()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        await _sut.ProcessHabitCreated(UserId);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_GrantsBaseXpPlusStreakBonus()
    {
        var user = CreateProUser();
        var initialXp = user.TotalXp;
        SetupUserLookup(user);
        SetupEarnedAchievements(
            AchievementDefinitions.Liftoff,
            AchievementDefinitions.LegendaryVolume,
            AchievementDefinitions.PerfectDay,
            AchievementDefinitions.PerfectWeek,
            AchievementDefinitions.PerfectMonth,
            AchievementDefinitions.EarlyBird,
            AchievementDefinitions.NightOwl,
            AchievementDefinitions.Comeback,
            AchievementDefinitions.BadHabitBreaker);

        var habit = CreateTestHabit();
        habit.Log(Today);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);
        SetupHabitLogs();

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        user.TotalXp.Should().Be(initialXp + 10 + 1);
    }

    [Fact]
    public async Task ProcessHabitLogged_StreakBonusApplied()
    {
        var user = CreateProUser();
        var initialXp = user.TotalXp;
        SetupUserLookup(user);
        SetupEarnedAchievements(
            AchievementDefinitions.Liftoff,
            AchievementDefinitions.LegendaryVolume,
            AchievementDefinitions.WeekWarrior,
            AchievementDefinitions.PerfectDay,
            AchievementDefinitions.PerfectWeek,
            AchievementDefinitions.PerfectMonth,
            AchievementDefinitions.EarlyBird,
            AchievementDefinitions.NightOwl,
            AchievementDefinitions.Comeback,
            AchievementDefinitions.BadHabitBreaker);

        var habit = CreateDailyHabitWithStreak(5);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);
        SetupHabitLogs();

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        user.TotalXp.Should().Be(initialXp + 10 + 5);
    }

    [Fact]
    public async Task ProcessHabitLogged_SundayStartInterval_PersistsOwnerBoundaryStreakReward()
    {
        var user = CreateProUser();
        user.SetWeekStartDay(0).IsSuccess.Should().BeTrue();
        var initialXp = user.TotalXp;
        SetupUserLookup(user);
        SetupEarnedAchievements(
            AchievementDefinitions.Liftoff,
            AchievementDefinitions.LegendaryVolume,
            AchievementDefinitions.PerfectDay,
            AchievementDefinitions.PerfectWeek,
            AchievementDefinitions.PerfectMonth,
            AchievementDefinitions.EarlyBird,
            AchievementDefinitions.NightOwl,
            AchievementDefinitions.Comeback,
            AchievementDefinitions.BadHabitBreaker);

        var anchor = new DateOnly(2026, 3, 1);
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Biweekly Friday",
            FrequencyUnit.Day,
            1,
            anchor,
            Days: [DayOfWeek.Friday],
            IntervalWeeks: 2)).Value;
        habit.Log(new DateOnly(2026, 3, 6), advanceDueDate: false);
        habit.Log(Today, advanceDueDate: false);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);
        SetupHabitLogs();

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        user.TotalXp.Should().Be(initialXp + 12);
        await _xpAwardLogRepo.Received(1).AddAsync(
            Arg.Is<XpAwardLog>(award =>
                award.Source == XpAwardSource.HabitLog
                && award.Amount == 12),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_BadHabit_NoXpNoAchievementsNoLevelNoStreak()
    {
        var user = CreateProUser();
        var initialXp = user.TotalXp;
        var initialLevel = user.Level;
        var initialStreak = user.CurrentStreak;
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit = CreateTestHabit(isBadHabit: true);
        habit.Log(Today);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);
        SetupHabitLogs();

        var result = await _sut.ProcessHabitLogged(UserId, habit.Id);

        result.Should().NotBeNull();
        result!.XpEarned.Should().Be(0);
        result.NewAchievementIds.Should().BeEmpty();
        user.TotalXp.Should().Be(initialXp);
        user.Level.Should().Be(initialLevel);
        user.CurrentStreak.Should().Be(initialStreak);
        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_BadHabit_ThirtyDayAbstinence_StillGrantsBadHabitBreaker()
    {
        var user = CreateProUser();
        var initialXp = user.TotalXp;
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var startDate = Today.AddDays(-30);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Skip Gym", FrequencyUnit.Day, 1, DueDate: startDate, IsBadHabit: true)).Value;
        typeof(Habit).GetProperty("CreatedAtUtc")!.SetValue(habit, startDate.ToDateTime(TimeOnly.MinValue));
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);
        SetupHabitLogs();

        var result = await _sut.ProcessHabitLogged(UserId, habit.Id);

        result.Should().NotBeNull();
        result!.XpEarned.Should().Be(0);
        result.NewAchievementIds.Should().Contain(AchievementDefinitions.BadHabitBreaker);
        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.BadHabitBreaker),
            Arg.Any<CancellationToken>());
        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.MonthlyMaster),
            Arg.Any<CancellationToken>());
        user.TotalXp.Should().BeGreaterThan(initialXp);
    }

    [Fact]
    public async Task ProcessHabitLogged_BadHabit_FreeUserWithFlag_GrantsBadHabitBreaker()
    {
        var user = CreateFreeUser();
        EnableFreeTierFlag();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var startDate = Today.AddDays(-30);
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Skip Gym", FrequencyUnit.Day, 1, DueDate: startDate, IsBadHabit: true)).Value;
        typeof(Habit).GetProperty("CreatedAtUtc")!.SetValue(habit, startDate.ToDateTime(TimeOnly.MinValue));
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);
        SetupHabitLogs();

        var result = await _sut.ProcessHabitLogged(UserId, habit.Id);

        result.Should().NotBeNull();
        result!.NewAchievementIds.Should().Contain(AchievementDefinitions.BadHabitBreaker);
        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.BadHabitBreaker),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitsLogged_ThreeHabits_LoadsSharedContextOnceAndGrantsXpPerHabit()
    {
        var user = CreateProUser();
        var initialXp = user.TotalXp;
        SetupUserLookup(user);
        SetupEarnedAchievements(AchievementDefinitions.Comeback);

        var habit1 = CreateTestHabit();
        habit1.Log(Today);
        var habit2 = CreateTestHabit();
        habit2.Log(Today);
        var habit3 = CreateTestHabit();
        habit3.Log(Today);
        SetupUserHabits(habit1, habit2, habit3);

        var results = await _sut.ProcessHabitsLogged(UserId, [habit1.Id, habit2.Id, habit3.Id]);

        results.Should().HaveCount(3);
        results.Should().AllSatisfy(r => r.XpEarned.Should().Be(10 + 1));
        user.TotalXp.Should().Be(initialXp + 3 * (10 + 1));
        await _habitRepo.Received(2).FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>());
        await _achievementRepo.Received(1).FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitsLogged_ThreeHabits_DoesNotGrantLiftoffSinceTotalLogsExceedOne()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var habit1 = CreateTestHabit();
        habit1.Log(Today);
        var habit2 = CreateTestHabit();
        habit2.Log(Today);
        var habit3 = CreateTestHabit();
        habit3.Log(Today);
        SetupUserHabits(habit1, habit2, habit3);

        await _sut.ProcessHabitsLogged(UserId, [habit1.Id, habit2.Id, habit3.Id]);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.Liftoff),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitsLogged_TwoHabitsCrossingSameThreshold_GrantsAchievementOnce()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();
        _habitLogRepo.AnyAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(true);

        var habit1 = CreateDailyHabitWithStreak(7);
        var habit2 = CreateDailyHabitWithStreak(7);
        SetupUserHabits(habit1, habit2);

        await _sut.ProcessHabitsLogged(UserId, [habit1.Id, habit2.Id]);

        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.WeekWarrior),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitsLogged_FreeUser_ReturnsEmptyWithoutSaving()
    {
        var user = CreateFreeUser();
        SetupUserLookup(user);

        var results = await _sut.ProcessHabitsLogged(UserId, [Guid.NewGuid()]);

        results.Should().BeEmpty();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitsLogged_NoMatchingHabits_ReturnsEmptyWithoutSaving()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();
        SetupUserHabits();

        var results = await _sut.ProcessHabitsLogged(UserId, [Guid.NewGuid()]);

        results.Should().BeEmpty();
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCompleted_Grants100Xp()
    {
        var user = CreateProUser();
        var initialXp = user.TotalXp;
        SetupUserLookup(user);
        SetupEarnedAchievements(
            AchievementDefinitions.GoalCrusher,
            AchievementDefinitions.Overachiever,
            AchievementDefinitions.DreamMaker);

        SetupCompletedGoalCount(0);

        await _sut.ProcessGoalCompleted(UserId, GoalId);

        user.TotalXp.Should().Be(initialXp + 100);
        await _xpAwardLogRepo.Received(1).AddAsync(
            Arg.Is<XpAwardLog>(award =>
                award.Source == XpAwardSource.GoalCompleted && award.SourceId == GoalId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCompleted_ExistingAwardForGoal_DoesNotAwardAgain()
    {
        var user = CreateProUser();
        var initialXp = user.TotalXp;
        SetupUserLookup(user);
        _xpAwardLogRepo.AnyAsync(
                Arg.Any<Expression<Func<XpAwardLog, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.ProcessGoalCompleted(UserId, GoalId);

        user.TotalXp.Should().Be(initialXp);
        await _xpAwardLogRepo.DidNotReceive().AddAsync(
            Arg.Any<XpAwardLog>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitCreated_NewAchievement_SendsNotification()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        SetupHabitCount(1);

        await _sut.ProcessHabitCreated(UserId);

        await _notificationRepo.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.Title.Contains("Achievement Unlocked")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitCreated_NewAchievement_SendsPush()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        SetupHabitCount(1);

        await _sut.ProcessHabitCreated(UserId);

        await _pushService.Received(1).SendToUserAsync(
            UserId,
            Arg.Is<string>(s => s.Contains("Achievement Unlocked")),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCompleted_ConcurrencyConflictThenSuccess_RetriesAndSendsPushOnce()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();
        SetupCompletedGoalCount(1);

        var saveAttempts = 0;
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                saveAttempts++;
                return saveAttempts == 1
                    ? throw new DbUpdateConcurrencyException("simulated stale token")
                    : Task.FromResult(1);
            });

        await _sut.ProcessGoalCompleted(UserId, GoalId);

        saveAttempts.Should().Be(2);
        _unitOfWork.Received(1).ResetTracking();
        await _pushService.Received(1).SendToUserAsync(
            UserId,
            Arg.Is<string>(s => s.Contains("Achievement Unlocked")),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCompleted_PersistentConflict_PropagatesAndNeverPushes()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();
        SetupCompletedGoalCount(1);

        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateConcurrencyException("simulated stale token"));

        var act = async () => await _sut.ProcessGoalCompleted(UserId, GoalId);

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        _unitOfWork.Received(2).ResetTracking();
        await _pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessOnboardingChecklist_ProUserAllThreeSignals_GrantsOnboardingCompleteOnceAndCompletes()
    {
        var user = CreateProUser();
        user.MarkFirstHabitCreated();
        user.MarkFirstHabitLogged();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        await _sut.ProcessOnboardingChecklistAsync(UserId, OnboardingChecklistSignal.AstraUsed);

        user.HasTriedAstra.Should().BeTrue();
        user.HasCompletedOnboardingChecklist.Should().BeTrue();
        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.OnboardingComplete),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessOnboardingChecklist_SignalMissing_DoesNotCompleteOrGrant()
    {
        var user = CreateProUser();
        user.MarkFirstHabitCreated();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        await _sut.ProcessOnboardingChecklistAsync(UserId, OnboardingChecklistSignal.HabitLogged);

        user.HasLoggedFirstHabit.Should().BeTrue();
        user.HasTriedAstra.Should().BeFalse();
        user.HasCompletedOnboardingChecklist.Should().BeFalse();
        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessOnboardingChecklist_FreeUserAllThreeSignals_CompletesButNoAchievement()
    {
        var user = CreateFreeUser();
        user.MarkFirstHabitCreated();
        user.MarkFirstHabitLogged();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        await _sut.ProcessOnboardingChecklistAsync(UserId, OnboardingChecklistSignal.AstraUsed);

        user.HasCompletedOnboardingChecklist.Should().BeTrue();
        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessOnboardingChecklist_FreeUserWithFlag_GrantsOnboardingComplete()
    {
        var user = CreateFreeUser();
        user.MarkFirstHabitCreated();
        user.MarkFirstHabitLogged();
        EnableFreeTierFlag();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        await _sut.ProcessOnboardingChecklistAsync(UserId, OnboardingChecklistSignal.AstraUsed);

        user.HasCompletedOnboardingChecklist.Should().BeTrue();
        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.OnboardingComplete),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessOnboardingChecklist_AlreadyComplete_EarlyOutWithoutSaving()
    {
        var user = CreateProUser();
        user.MarkFirstHabitCreated();
        user.MarkFirstHabitLogged();
        user.MarkAstraUsed();
        user.CompleteOnboardingChecklist();
        SetupUserLookup(user);

        await _sut.ProcessOnboardingChecklistAsync(UserId, OnboardingChecklistSignal.AstraUsed);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessOnboardingChecklist_AlreadyEarnedAchievement_CompletesWithoutDoubleGrant()
    {
        var user = CreateProUser();
        user.MarkFirstHabitCreated();
        user.MarkFirstHabitLogged();
        SetupUserLookup(user);
        SetupEarnedAchievements(AchievementDefinitions.OnboardingComplete);

        await _sut.ProcessOnboardingChecklistAsync(UserId, OnboardingChecklistSignal.AstraUsed);

        user.HasCompletedOnboardingChecklist.Should().BeTrue();
        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.OnboardingComplete),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessOnboardingChecklist_UserNotFound_DoesNothing()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        await _sut.ProcessOnboardingChecklistAsync(UserId, OnboardingChecklistSignal.AstraUsed);

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitLogged_FreeUserWithFlag_EarnsXpAndLiftoff()
    {
        var user = CreateFreeUser();
        var initialXp = user.TotalXp;
        EnableFreeTierFlag();
        SetupUserLookup(user);
        SetupEarnedAchievements(AchievementDefinitions.Comeback);

        var habit = CreateTestHabit();
        habit.Log(Today);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);
        SetupHabitLogs();

        var result = await _sut.ProcessHabitLogged(UserId, habit.Id);

        result.Should().NotBeNull();
        result!.XpEarned.Should().Be(10 + 1);
        result.NewAchievementIds.Should().Contain(AchievementDefinitions.Liftoff);
        var achievementXp = AchievementDefinitions.All.Single(a => a.Id == AchievementDefinitions.Liftoff).XpReward;
        user.TotalXp.Should().Be(initialXp + 10 + 1 + achievementXp);
        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.Liftoff),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitsLogged_FreeUserWithFlag_EarnsXpAndLiftoff()
    {
        var user = CreateFreeUser();
        var initialXp = user.TotalXp;
        EnableFreeTierFlag();
        SetupUserLookup(user);
        SetupEarnedAchievements(AchievementDefinitions.Comeback);

        var habit = CreateTestHabit();
        habit.Log(Today);
        SetupUserHabits(habit);

        var results = await _sut.ProcessHabitsLogged(UserId, [habit.Id]);

        results.Should().ContainSingle();
        results[0].XpEarned.Should().Be(10 + 1);
        results[0].NewAchievementIds.Should().Contain(AchievementDefinitions.Liftoff);
        var achievementXp = AchievementDefinitions.All.Single(a => a.Id == AchievementDefinitions.Liftoff).XpReward;
        user.TotalXp.Should().Be(initialXp + 10 + 1 + achievementXp);
        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.Liftoff),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessGoalCompleted_FreeUserWithFlag_EarnsXpAndGoalCrusher()
    {
        var user = CreateFreeUser();
        var initialXp = user.TotalXp;
        EnableFreeTierFlag();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();
        SetupCompletedGoalCount(1);

        await _sut.ProcessGoalCompleted(UserId, GoalId);

        var achievementXp = AchievementDefinitions.All.Single(a => a.Id == AchievementDefinitions.GoalCrusher).XpReward;
        user.TotalXp.Should().Be(initialXp + 100 + achievementXp);
        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.GoalCrusher),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGrantAchievementsAsync_FreeUserNewAchievement_GrantsAwardsXpAndNotifies()
    {
        var user = CreateFreeUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        var granted = await _sut.TryGrantAchievementsAsync(UserId, [AchievementDefinitions.ShowOff]);

        granted.Should().ContainSingle().Which.Should().Be(AchievementDefinitions.ShowOff);
        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.ShowOff),
            Arg.Any<CancellationToken>());
        user.TotalXp.Should().Be(75);
        await _notificationRepo.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.Title.Contains("Achievement Unlocked")),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileFoundingAchievementsAsync_ThreeEligible_GrantsExactlyThree()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();
        _foundingAchievementReader.ReadEvidenceAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new FoundingAchievementEvidence(
                HasHabitLog: true,
                HasTopLevelHabit: false,
                HasGoal: true,
                HasCompletedGoal: false,
                HasCompletedOnboardingChecklist: true));

        var granted = await _sut.ReconcileFoundingAchievementsAsync(UserId);

        granted.Should().BeEquivalentTo(
            AchievementDefinitions.Liftoff,
            AchievementDefinitions.MissionControl,
            AchievementDefinitions.OnboardingComplete);
        await _achievementRepo.Received(3).AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
        user.TotalXp.Should().Be(
            AchievementDefinitions.GetById(AchievementDefinitions.Liftoff)!.XpReward
            + AchievementDefinitions.GetById(AchievementDefinitions.MissionControl)!.XpReward
            + AchievementDefinitions.GetById(AchievementDefinitions.OnboardingComplete)!.XpReward);
    }

    [Fact]
    public async Task ReconcileFoundingAchievementsAsync_RunTwice_SecondRunIsNoOp()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        var foundingAchievements = new[]
        {
            AchievementDefinitions.Liftoff,
            AchievementDefinitions.FirstOrbit,
            AchievementDefinitions.MissionControl,
            AchievementDefinitions.GoalCrusher,
            AchievementDefinitions.OnboardingComplete
        };
        _achievementRepo.FindAsync(
                Arg.Any<Expression<Func<UserAchievement, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new List<UserAchievement>(),
                new List<UserAchievement>(),
                foundingAchievements.Select(id => UserAchievement.Create(UserId, id)).ToList());
        _foundingAchievementReader.ReadEvidenceAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new FoundingAchievementEvidence(true, true, true, true, true));

        var first = await _sut.ReconcileFoundingAchievementsAsync(UserId);
        var second = await _sut.ReconcileFoundingAchievementsAsync(UserId);

        first.Should().BeEquivalentTo(foundingAchievements);
        second.Should().BeEmpty();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _foundingAchievementReader.Received(1).ReadEvidenceAsync(
            UserId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileFoundingAchievementsAsync_FlagCycles_ReexaminesPreviouslyUnlockedUser()
    {
        var user = CreateFreeUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();
        EnableFreeTierFlag();
        _foundingAchievementReader.ReadEvidenceAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new FoundingAchievementEvidence(true, false, false, false, false));

        var first = await _sut.ReconcileFoundingAchievementsAsync(UserId);

        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _foundingAchievementReader.ReadEvidenceAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new FoundingAchievementEvidence(true, false, true, false, false));
        var whileLocked = await _sut.ReconcileFoundingAchievementsAsync(UserId);

        EnableFreeTierFlag();
        SetupEarnedAchievements(AchievementDefinitions.Liftoff);
        var afterUnlock = await _sut.ReconcileFoundingAchievementsAsync(UserId);

        first.Should().ContainSingle().Which.Should().Be(AchievementDefinitions.Liftoff);
        whileLocked.Should().BeEmpty();
        afterUnlock.Should().ContainSingle().Which.Should().Be(AchievementDefinitions.MissionControl);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileFoundingAchievementsAsync_UniqueConflict_RereadsEvidenceBeforeRetry()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();
        _foundingAchievementReader.ReadEvidenceAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(
                new FoundingAchievementEvidence(true, false, false, false, false),
                new FoundingAchievementEvidence(false, false, false, false, false));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException(
                "duplicate key value violates unique constraint",
                new PostgresException(
                    "duplicate key",
                    "ERROR",
                    "ERROR",
                    PostgresErrorCodes.UniqueViolation)));

        var granted = await _sut.ReconcileFoundingAchievementsAsync(UserId);

        granted.Should().BeEmpty();
        await _foundingAchievementReader.Received(2).ReadEvidenceAsync(
            UserId,
            Arg.Any<CancellationToken>());
        _unitOfWork.Received(1).ResetTracking();
        await _pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessHabitsLogged_ReconciliationWinsBadgeRace_RetriesAndCommitsHabitXp()
    {
        var losingAttemptUser = CreateProUser();
        var persistedWinnerUser = CreateProUser();
        var liftoffXp = AchievementDefinitions.GetById(AchievementDefinitions.Liftoff)!.XpReward;
        persistedWinnerUser.AddXp(liftoffXp);
        _userRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(losingAttemptUser, persistedWinnerUser);

        var alreadyEarned = AchievementDefinitions.Active
            .Select(definition => definition.Id)
            .Where(id => id != AchievementDefinitions.Liftoff)
            .ToArray();
        _achievementRepo.FindAsync(
                Arg.Any<Expression<Func<UserAchievement, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(
                alreadyEarned.Select(id => UserAchievement.Create(UserId, id)).ToList(),
                alreadyEarned
                    .Append(AchievementDefinitions.Liftoff)
                    .Select(id => UserAchievement.Create(UserId, id))
                    .ToList());

        var habit = CreateTestHabit();
        habit.Log(Today);
        SetupUserHabits(habit);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<int>(new DbUpdateException(
                    "duplicate key value violates unique constraint",
                    new PostgresException(
                        "duplicate key",
                        "ERROR",
                        "ERROR",
                        PostgresErrorCodes.UniqueViolation))),
                Task.FromResult(1));

        var results = await _sut.ProcessHabitsLogged(UserId, [habit.Id]);

        results.Should().ContainSingle().Which.XpEarned.Should().Be(11);
        persistedWinnerUser.TotalXp.Should().Be(liftoffXp + 11);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
        _unitOfWork.Received(1).ResetTracking();
        await _achievementRepo.Received(1).AddAsync(
            Arg.Is<UserAchievement>(achievement => achievement.AchievementId == AchievementDefinitions.Liftoff),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGrantAchievementsAsync_AlreadyEarned_IdempotentNoSave()
    {
        var user = CreateFreeUser();
        SetupUserLookup(user);
        SetupEarnedAchievements(AchievementDefinitions.ShowOff);

        var granted = await _sut.TryGrantAchievementsAsync(UserId, [AchievementDefinitions.ShowOff]);

        granted.Should().BeEmpty();
        await _achievementRepo.DidNotReceive().AddAsync(Arg.Any<UserAchievement>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGrantAchievementsAsync_ConcurrentDuplicate_ReloadsPersistedWinnerWithoutDuplicateXp()
    {
        var losingAttemptUser = CreateFreeUser();
        var persistedWinnerUser = CreateFreeUser();
        _userRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(losingAttemptUser, persistedWinnerUser);
        _achievementRepo.FindAsync(
                Arg.Any<Expression<Func<UserAchievement, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new List<UserAchievement>(),
                new List<UserAchievement>
                {
                    UserAchievement.Create(UserId, AchievementDefinitions.ShowOff)
                });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException(
                "duplicate key value violates unique constraint",
                new PostgresException(
                    "duplicate key",
                    "ERROR",
                    "ERROR",
                    PostgresErrorCodes.UniqueViolation)));

        var granted = await _sut.TryGrantAchievementsAsync(
            UserId,
            [AchievementDefinitions.ShowOff]);

        granted.Should().BeEmpty();
        persistedWinnerUser.TotalXp.Should().Be(0);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _unitOfWork.Received(1).ResetTracking();
        await _pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
