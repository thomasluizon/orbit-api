using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Services;
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
    private readonly IPushNotificationService _pushService = Substitute.For<IPushNotificationService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly GamificationService _sut;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public GamificationServiceTests()
    {
        _sut = new GamificationService(
            _userRepo, _habitRepo, _habitLogRepo, _goalRepo,
            _achievementRepo, _notificationRepo, _pushService, _userDateService, _unitOfWork,
            Substitute.For<ILogger<GamificationService>>());

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
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
        var habit = Habit.Create(
            userId ?? UserId, "Test Habit", FrequencyUnit.Day, 1,
            dueDate: Today, isBadHabit: isBadHabit).Value;

        // Backdate CreatedAtUtc so HabitMetricsCalculator generates expected dates
        // covering Today (the test's fixed date, not the actual DateTime.UtcNow)
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
        var habit = Habit.Create(UserId, "Daily", FrequencyUnit.Day, 1, dueDate: startDate).Value;

        // HabitMetricsCalculator uses CreatedAtUtc as the earliest boundary for expected dates.
        // Since Create() sets it to DateTime.UtcNow (today), we need to backdate it.
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
        var habit = Habit.Create(UserId, "Habit with logs", FrequencyUnit.Day, 1,
            dueDate: startDate).Value;

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

    // --- ProcessHabitCreated: first_orbit ---

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

    // --- ProcessHabitLogged: liftoff ---

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

    // --- Streak achievements ---

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

    // --- Volume achievements ---

    [Fact]
    public async Task ProcessHabitLogged_10Completions_GrantsGettingMomentum()
    {
        var user = CreateProUser();
        SetupUserLookup(user);
        SetupNoEarnedAchievements();

        // Service now loads all habits via FindAsync and sums logs from the collection.
        // Create a habit with 10 logs total (including the one being logged)
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

    // --- Goal achievements ---

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

        await _sut.ProcessGoalCompleted(UserId);

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

        await _sut.ProcessGoalCompleted(UserId);

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

        await _sut.ProcessGoalCompleted(UserId);

        await _achievementRepo.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == AchievementDefinitions.DreamMaker),
            Arg.Any<CancellationToken>());
    }

    // --- Does not grant already-earned achievements ---

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

    // --- Skips if user doesn't have Pro access ---

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

        await _sut.ProcessGoalCompleted(UserId);

        await _achievementRepo.DidNotReceive().AddAsync(
            Arg.Any<UserAchievement>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
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

    // --- XP calculation ---

    [Fact]
    public async Task ProcessHabitLogged_GrantsBaseXpPlusStreakBonus()
    {
        var user = CreateProUser();
        var initialXp = user.TotalXp;
        SetupUserLookup(user);
        // Pre-earn all achievements that could be triggered, so only base+streak XP is added
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

        // Base XP = 10, streak bonus = 1 (single log = 1 streak)
        user.TotalXp.Should().Be(initialXp + 10 + 1);
    }

    [Fact]
    public async Task ProcessHabitLogged_StreakBonusApplied()
    {
        var user = CreateProUser();
        var initialXp = user.TotalXp;
        SetupUserLookup(user);
        // Pre-earn all triggerable achievements
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

        // Create habit with 5-day streak (logs forward from startDate)
        var habit = CreateDailyHabitWithStreak(5);
        SetupHabitWithLogs(habit);
        SetupUserHabits(habit);
        SetupHabitLogs();

        await _sut.ProcessHabitLogged(UserId, habit.Id);

        // XP = 10 base + 5 streak bonus
        user.TotalXp.Should().Be(initialXp + 10 + 5);
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

        await _sut.ProcessGoalCompleted(UserId);

        user.TotalXp.Should().Be(initialXp + 100);
    }

    // --- Notifications ---

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
}
