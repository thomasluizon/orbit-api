using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Backfill;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Services;

public sealed class AchievementEligibilityReconciliationServiceTests
{
    private static readonly DateOnly Today = new(2026, 8, 22);

    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepository = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<Goal> _goalRepository = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly AchievementEligibilityReconciliationService _sut;

    public AchievementEligibilityReconciliationServiceTests()
    {
        _sut = new AchievementEligibilityReconciliationService(
            _userRepository,
            _habitRepository,
            _habitLogRepository,
            _goalRepository,
            _achievementRepository,
            _gamificationService);

        ArrangePersistedState([], [], [], [], []);
        _gamificationService.TryGrantAchievementsAsync(
                Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<string>>(
                call.ArgAt<IReadOnlyList<string>>(1).ToList()));
    }

    [Fact]
    public async Task ReconcileAll_MultiplePersistedRowsAndCompletedChecklist_RequestsAllFiveAchievements()
    {
        var user = CreateFreeUser();
        user.CompleteOnboardingChecklist();
        var habits = new[] { CreateHabit(user.Id, "Habit A"), CreateHabit(user.Id, "Habit B") };
        foreach (var habit in habits)
            habit.Log(Today, advanceDueDate: false);
        var goals = new[] { CreateGoal(user.Id, "Goal A", completed: true), CreateGoal(user.Id, "Goal B", completed: true) };
        ArrangePersistedState([user], habits, habits.SelectMany(habit => habit.Logs).ToList(), goals, []);

        var result = await _sut.ReconcileAllAsync();

        result.Should().Be(new AchievementEligibilityReconciliationResult(1, 5));
        await _gamificationService.Received(1).TryGrantAchievementsAsync(
            user.Id,
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[]
            {
                AchievementDefinitions.Liftoff,
                AchievementDefinitions.FirstOrbit,
                AchievementDefinitions.MissionControl,
                AchievementDefinitions.GoalCrusher,
                AchievementDefinitions.OnboardingComplete
            })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAll_AlreadyEarnedAchievement_DoesNotRequestOrCountItAgain()
    {
        var user = CreateFreeUser();
        user.CompleteOnboardingChecklist();
        var habit = CreateHabit(user.Id);
        habit.Log(Today, advanceDueDate: false);
        var goal = CreateGoal(user.Id, completed: true);
        var earned = UserAchievement.Create(user.Id, AchievementDefinitions.Liftoff);
        ArrangePersistedState([user], [habit], habit.Logs, [goal], [earned]);

        var result = await _sut.ReconcileAllAsync();

        result.Should().Be(new AchievementEligibilityReconciliationResult(1, 4));
        await _gamificationService.Received(1).TryGrantAchievementsAsync(
            user.Id,
            Arg.Is<IReadOnlyList<string>>(ids => ids.Count == 4 && !ids.Contains(AchievementDefinitions.Liftoff)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAll_RunTwice_SecondRunGrantsNothingAndAwardsNoAdditionalXp()
    {
        var user = CreateFreeUser();
        var habit = CreateHabit(user.Id);
        habit.Log(Today, advanceDueDate: false);
        var earned = new List<UserAchievement>();
        ArrangePersistedState([user], [habit], habit.Logs, [], earned);
        var awardedXp = 0;

        _gamificationService.TryGrantAchievementsAsync(
                user.Id,
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.ArgAt<IReadOnlyList<string>>(1).ToList();
                foreach (var id in ids)
                {
                    earned.Add(UserAchievement.Create(user.Id, id));
                    awardedXp += AchievementDefinitions.GetById(id)!.XpReward;
                }

                return Task.FromResult<IReadOnlyList<string>>(ids);
            });

        var first = await _sut.ReconcileAllAsync();
        var xpAfterFirstRun = awardedXp;
        var second = await _sut.ReconcileAllAsync();

        first.Should().Be(new AchievementEligibilityReconciliationResult(1, 2));
        second.Should().Be(new AchievementEligibilityReconciliationResult(0, 0));
        awardedXp.Should().Be(xpAfterFirstRun);
        await _gamificationService.Received(1).TryGrantAchievementsAsync(
            user.Id,
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAll_NoPositiveHabitLog_DoesNotRequestLiftoff()
    {
        var user = CreateFreeUser();
        var habit = CreateHabit(user.Id);
        ArrangePersistedState([user], [habit], [], [], []);

        await _sut.ReconcileAllAsync();

        await _gamificationService.Received(1).TryGrantAchievementsAsync(
            user.Id,
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { AchievementDefinitions.FirstOrbit })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAll_OnlyCompletedSubHabit_DoesNotRequestFirstOrbit()
    {
        var user = CreateFreeUser();
        var parentId = Guid.NewGuid();
        var child = CreateHabit(user.Id, parentHabitId: parentId);
        child.Log(Today, advanceDueDate: false);
        ArrangePersistedState([user], [child], child.Logs, [], []);

        await _sut.ReconcileAllAsync();

        await _gamificationService.Received(1).TryGrantAchievementsAsync(
            user.Id,
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { AchievementDefinitions.Liftoff })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAll_NoGoals_DoesNotRequestGoalAchievements()
    {
        var user = CreateFreeUser();
        ArrangePersistedState([user], [], [], [], []);

        var result = await _sut.ReconcileAllAsync();

        result.Should().Be(new AchievementEligibilityReconciliationResult(0, 0));
        await _gamificationService.DidNotReceive().TryGrantAchievementsAsync(
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAll_OnlyActiveGoals_DoesNotRequestGoalCrusher()
    {
        var user = CreateFreeUser();
        var goals = new[] { CreateGoal(user.Id, "Goal A"), CreateGoal(user.Id, "Goal B") };
        ArrangePersistedState([user], [], [], goals, []);

        await _sut.ReconcileAllAsync();

        await _gamificationService.Received(1).TryGrantAchievementsAsync(
            user.Id,
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { AchievementDefinitions.MissionControl })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAll_IncompleteOnboardingChecklist_DoesNotRequestAllSystemsGo()
    {
        var user = CreateFreeUser();
        var habit = CreateHabit(user.Id);
        ArrangePersistedState([user], [habit], [], [], []);

        await _sut.ReconcileAllAsync();

        await _gamificationService.Received(1).TryGrantAchievementsAsync(
            user.Id,
            Arg.Is<IReadOnlyList<string>>(ids => !ids.Contains(AchievementDefinitions.OnboardingComplete)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAll_ProAccountWithCompletedChecklist_ReconcilesPersistedEligibility()
    {
        var user = User.Create("Pro User", "pro@example.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddYears(1));
        user.CompleteOnboardingChecklist();
        ArrangePersistedState([user], [], [], [], []);

        var result = await _sut.ReconcileAllAsync();

        result.Should().Be(new AchievementEligibilityReconciliationResult(1, 1));
        await _gamificationService.Received(1).TryGrantAchievementsAsync(
            user.Id,
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { AchievementDefinitions.OnboardingComplete })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAll_SoftDeletedMilestoneRecords_RemainHistoricalEligibilityEvidence()
    {
        var user = CreateFreeUser();
        var habit = CreateHabit(user.Id);
        habit.Log(Today, advanceDueDate: false);
        var log = habit.Logs.Single();
        log.SoftDelete();
        habit.SoftDelete();
        var goal = CreateGoal(user.Id, completed: true);
        goal.SoftDelete();
        ArrangePersistedState([user], [habit], [log], [goal], []);

        var result = await _sut.ReconcileAllAsync();

        result.Should().Be(new AchievementEligibilityReconciliationResult(1, 4));
        await _gamificationService.Received(1).TryGrantAchievementsAsync(
            user.Id,
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[]
            {
                AchievementDefinitions.Liftoff,
                AchievementDefinitions.FirstOrbit,
                AchievementDefinitions.MissionControl,
                AchievementDefinitions.GoalCrusher
            })),
            Arg.Any<CancellationToken>());
        await _habitRepository.Received(1).FindTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>());
        await _habitLogRepository.Received(1).FindTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<HabitLog, bool>>>(),
            Arg.Any<CancellationToken>());
        await _goalRepository.Received(1).FindTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>());
    }

    private void ArrangePersistedState(
        IReadOnlyCollection<User> users,
        IReadOnlyCollection<Habit> habits,
        IReadOnlyCollection<HabitLog> habitLogs,
        IReadOnlyCollection<Goal> goals,
        IReadOnlyCollection<UserAchievement> achievements)
    {
        _userRepository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(users.ToList());
        _habitRepository.FindTrackedIgnoringFiltersAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(habits.ToList());
        _habitLogRepository.FindTrackedIgnoringFiltersAsync(
                Arg.Any<Expression<Func<HabitLog, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(habitLogs.ToList());
        _goalRepository.FindTrackedIgnoringFiltersAsync(
                Arg.Any<Expression<Func<Goal, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(goals.ToList());
        _achievementRepository.FindAsync(
                Arg.Any<Expression<Func<UserAchievement, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => achievements.ToList());
    }

    private static User CreateFreeUser()
    {
        var user = User.Create("Free User", $"{Guid.NewGuid():N}@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    private static Habit CreateHabit(
        Guid userId,
        string title = "Habit",
        Guid? parentHabitId = null)
    {
        return Habit.Create(new HabitCreateParams(
            userId,
            title,
            FrequencyUnit.Day,
            1,
            DueDate: Today,
            ParentHabitId: parentHabitId)).Value;
    }

    private static Goal CreateGoal(Guid userId, string title = "Goal", bool completed = false)
    {
        var goal = Goal.Create(userId, title, 1, "unit").Value;
        if (completed)
            goal.MarkCompleted();
        return goal;
    }
}
