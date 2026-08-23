using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Goals.Services;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// DB-backed regressions for passive derived-goal reconciliation: linked Standard and Streak goals
/// advance from habit logs without a request, auto-complete at target, and route completion through
/// gamification exactly once.
/// </summary>
public class StreakGoalSyncServiceTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task SyncActiveGoals_StreakAdvancesCurrentValueFromLinkedHabitLogs()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = CreateDailyHabitLoggedLastDays(user.Id, days: 3);
        var goal = CreateStreakGoal(user.Id, target: 7);
        goal.AddHabit(habit);

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, Substitute.For<IGamificationService>());
        await service.SyncActiveGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.CurrentValue.Should().Be(3);
        reloaded.Status.Should().Be(GoalStatus.Active);
        reloaded.StreakSyncedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncActiveGoals_StreakReachesTarget_AutoCompletesAndGamifiesOnce()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = CreateDailyHabitLoggedLastDays(user.Id, days: 3);
        var goal = CreateStreakGoal(user.Id, target: 3);
        goal.AddHabit(habit);

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();

        var gamification = Substitute.For<IGamificationService>();
        var service = CreateService(dbContext, gamification);
        await service.SyncActiveGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.Status.Should().Be(GoalStatus.Completed);
        reloaded.CompletedAtUtc.Should().NotBeNull();
        await gamification.Received(1).ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncActiveGoals_LinkedStandardAtTarget_AutoCompletesAndGamifiesOnce()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var dbContext = factory.Context;
        var user = User.Create("Thomas", "thomas@test.com").Value;
        user.GrantLifetimePro();
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Exercise", FrequencyUnit.Day, 2, DueDate: Today, IsFlexible: true)).Value;
        var goal = Goal.Create(user.Id, "Exercise twice", 2, "sessions").Value;
        goal.AddHabit(habit);
        habit.Log(Today);
        habit.Log(Today.AddDays(-1));

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();

        var unitOfWork = CreateUnitOfWork(dbContext);
        var service = CreateService(dbContext, CreateGamificationService(dbContext, unitOfWork), unitOfWork);
        await service.SyncActiveGoals(CancellationToken.None);
        await service.SyncActiveGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.CurrentValue.Should().Be(2);
        reloaded.Status.Should().Be(GoalStatus.Completed);
        reloaded.CompletedAtUtc.Should().NotBeNull();
        (await dbContext.XpAwardLogs.CountAsync(x =>
            x.UserId == user.Id && x.Source == XpAwardSource.GoalCompleted)).Should().Be(1);
        (await dbContext.UserAchievements.CountAsync(a =>
            a.UserId == user.Id && a.AchievementId == AchievementDefinitions.GoalCrusher)).Should().Be(1);
    }

    [Fact]
    public async Task SyncActiveGoals_GamificationFailure_RollsBackAndRetriesCompletion()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var dbContext = factory.Context;
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            user.Id, "Exercise", FrequencyUnit.Day, 2, DueDate: Today, IsFlexible: true)).Value;
        var goal = Goal.Create(user.Id, "Exercise twice", 2, "sessions").Value;
        goal.AddHabit(habit);
        habit.Log(Today);
        habit.Log(Today.AddDays(-1));

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();

        var attempts = 0;
        var gamification = Substitute.For<IGamificationService>();
        gamification.ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>())
            .Returns(_ => ++attempts == 1
                ? Task.FromException(new InvalidOperationException("Forced award failure"))
                : Task.CompletedTask);
        var service = CreateService(dbContext, gamification);

        var firstSweep = () => service.SyncActiveGoals(CancellationToken.None);
        await firstSweep.Should().ThrowAsync<InvalidOperationException>();

        var afterFailure = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        afterFailure.CurrentValue.Should().Be(0);
        afterFailure.Status.Should().Be(GoalStatus.Active);
        afterFailure.CompletedAtUtc.Should().BeNull();

        await service.SyncActiveGoals(CancellationToken.None);

        var afterRetry = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        afterRetry.CurrentValue.Should().Be(2);
        afterRetry.Status.Should().Be(GoalStatus.Completed);
        afterRetry.CompletedAtUtc.Should().NotBeNull();
        await gamification.Received(2).ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncActiveGoals_ConcurrencyConflict_RollsBackAndRetriesNextSweep()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var dbContext = factory.Context;
        var firstUser = User.Create("Alice", "alice@test.com").Value;
        var secondUser = User.Create("Bob", "bob@test.com").Value;
        var firstHabit = CreateStandardHabit(firstUser.Id);
        var secondHabit = CreateStandardHabit(secondUser.Id);
        var firstGoal = Goal.Create(firstUser.Id, "Exercise twice", 2, "sessions").Value;
        var secondGoal = Goal.Create(secondUser.Id, "Exercise twice", 2, "sessions").Value;
        firstGoal.AddHabit(firstHabit);
        secondGoal.AddHabit(secondHabit);
        firstHabit.Log(Today);
        firstHabit.Log(Today.AddDays(-1));
        secondHabit.Log(Today);
        secondHabit.Log(Today.AddDays(-1));

        dbContext.Users.AddRange(firstUser, secondUser);
        dbContext.Habits.AddRange(firstHabit, secondHabit);
        dbContext.Goals.AddRange(firstGoal, secondGoal);
        await dbContext.SaveChangesAsync();

        var attempts = 0;
        var gamification = Substitute.For<IGamificationService>();
        gamification.ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++attempts == 1
                ? Task.FromException(new DbUpdateConcurrencyException("Forced completion conflict"))
                : Task.CompletedTask);
        var service = CreateService(dbContext, gamification);

        var firstSweep = () => service.SyncActiveGoals(CancellationToken.None);
        await firstSweep.Should().ThrowAsync<DbUpdateConcurrencyException>();

        var afterConflict = await dbContext.Goals.AsNoTracking().ToListAsync();
        afterConflict.Should().OnlyContain(g => g.Status == GoalStatus.Active);
        await gamification.Received(1)
            .ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        await service.SyncActiveGoals(CancellationToken.None);

        var afterRetry = await dbContext.Goals.AsNoTracking().ToListAsync();
        afterRetry.Should().OnlyContain(g => g.Status == GoalStatus.Completed);
        await gamification.Received(3)
            .ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncActiveGoals_StreakAlreadySyncedToday_LeavesValueAndSkipsGamification()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var habit = CreateDailyHabitLoggedLastDays(user.Id, days: 3);
        var goal = CreateStreakGoal(user.Id, target: 7);
        goal.AddHabit(habit);
        goal.SyncStreakProgress(3);

        dbContext.Users.Add(user);
        dbContext.Habits.Add(habit);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();

        var gamification = Substitute.For<IGamificationService>();
        var service = CreateService(dbContext, gamification);
        await service.SyncActiveGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.CurrentValue.Should().Be(3);
        await gamification.DidNotReceive().ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncActiveGoals_MultipleCompletingStreakGoals_PersistsEachAndGamifiesEachUser()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var firstUser = User.Create("Alice", "alice@test.com").Value;
        var secondUser = User.Create("Bob", "bob@test.com").Value;
        var firstHabit = CreateDailyHabitLoggedLastDays(firstUser.Id, days: 3);
        var secondHabit = CreateDailyHabitLoggedLastDays(secondUser.Id, days: 3);
        var firstGoal = CreateStreakGoal(firstUser.Id, target: 3);
        var secondGoal = CreateStreakGoal(secondUser.Id, target: 3);
        firstGoal.AddHabit(firstHabit);
        secondGoal.AddHabit(secondHabit);

        dbContext.Users.AddRange(firstUser, secondUser);
        dbContext.Habits.AddRange(firstHabit, secondHabit);
        dbContext.Goals.AddRange(firstGoal, secondGoal);
        await dbContext.SaveChangesAsync();

        var gamification = Substitute.For<IGamificationService>();
        var service = CreateService(dbContext, gamification);
        await service.SyncActiveGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().ToListAsync();
        reloaded.Should().OnlyContain(g => g.Status == GoalStatus.Completed);
        await gamification.Received(1).ProcessGoalCompleted(firstUser.Id, Arg.Any<CancellationToken>());
        await gamification.Received(1).ProcessGoalCompleted(secondUser.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncActiveGoals_StreakWithNoLinkedHabits_LeavesGoalUntouched()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var goal = CreateStreakGoal(user.Id, target: 7);

        dbContext.Users.Add(user);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, Substitute.For<IGamificationService>());
        await service.SyncActiveGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.CurrentValue.Should().Be(0);
        reloaded.StreakSyncedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task SyncActiveGoals_ManualStandardWithNoLinkedHabits_LeavesGoalUntouched()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var goal = Goal.Create(user.Id, "Exercise seven times", 7, "sessions").Value;
        goal.UpdateProgress(2);

        dbContext.Users.Add(user);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();

        var gamification = Substitute.For<IGamificationService>();
        var service = CreateService(dbContext, gamification);
        await service.SyncActiveGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.CurrentValue.Should().Be(2);
        reloaded.Status.Should().Be(GoalStatus.Active);
        reloaded.IsProgressDerived.Should().BeFalse();
        await gamification.DidNotReceive().ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncActiveGoals_QueryCount_IsInvariantToCandidateVolume()
    {
        var small = await CountSweepQueriesAsync(candidateCount: 2);
        var large = await CountSweepQueriesAsync(candidateCount: 25);

        large.Should().Be(small);
        large.Should().BeLessThanOrEqualTo(4);
    }

    private static Habit CreateDailyHabitLoggedLastDays(Guid userId, int days)
    {
        var startDate = Today.AddDays(-(days - 1));
        var habit = Habit.Create(new HabitCreateParams(
            userId, "Meditate", FrequencyUnit.Day, 1, DueDate: startDate)).Value;
        SetCreatedAtUtc(habit, startDate);
        for (var offset = days - 1; offset >= 0; offset--)
            habit.Log(Today.AddDays(-offset), advanceDueDate: false);
        return habit;
    }

    private static Goal CreateStreakGoal(Guid userId, decimal target) =>
        Goal.Create(new Goal.CreateGoalParams(
            userId, "Daily streak", target, "days", Type: GoalType.Streak)).Value;

    private static Habit CreateStandardHabit(Guid userId) =>
        Habit.Create(new HabitCreateParams(
            userId, "Exercise", FrequencyUnit.Day, 2, DueDate: Today, IsFlexible: true)).Value;

    private static async Task<int> CountSweepQueriesAsync(int candidateCount)
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var dbContext = factory.Context;
        var user = User.Create("Query User", "query@example.com").Value;
        dbContext.Users.Add(user);

        for (var index = 0; index < candidateCount; index++)
        {
            var habit = CreateStandardHabit(user.Id);
            var goal = CreateStreakGoal(user.Id, target: 10);
            goal.AddHabit(habit);
            goal.SyncStreakProgress(1).IsSuccess.Should().BeTrue();
            dbContext.Habits.Add(habit);
            dbContext.Goals.Add(goal);
        }

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var service = CreateService(dbContext, Substitute.For<IGamificationService>());

        counter.Reset();
        await service.SyncActiveGoals(CancellationToken.None);
        return counter.CommandCount;
    }

    private static void SetCreatedAtUtc(Habit habit, DateOnly localDate)
    {
        typeof(Habit)
            .GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    private static OrbitDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"StreakGoalSyncServiceTests_{Guid.NewGuid()}")
            .Options;
        return new OrbitDbContext(options);
    }

    private static StreakGoalSyncService CreateService(
        OrbitDbContext dbContext,
        IGamificationService gamificationService,
        IUnitOfWork? unitOfWork = null)
    {
        unitOfWork ??= CreateUnitOfWork(dbContext);
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        var goalCompletionService = new GoalCompletionService(
            new GenericRepository<Goal>(dbContext),
            gamificationService,
            unitOfWork);
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(gamificationService)
            .AddSingleton(unitOfWork)
            .AddSingleton<IUserDateService>(userDateService)
            .AddSingleton<IGoalCompletionService>(goalCompletionService)
            .BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new StreakGoalSyncService(
            scopeFactory,
            NullLogger<StreakGoalSyncService>.Instance,
            new ConfigurationBuilder().Build());
    }

    private static IUnitOfWork CreateUnitOfWork(OrbitDbContext dbContext) =>
        new UnitOfWork(dbContext, new DatabaseConnectionSettings());

    private static IGamificationService CreateGamificationService(
        OrbitDbContext dbContext,
        IUnitOfWork unitOfWork)
    {
        var repos = new GamificationRepositories(
            new GenericRepository<User>(dbContext),
            new GenericRepository<Habit>(dbContext),
            new GenericRepository<HabitLog>(dbContext),
            new GenericRepository<Goal>(dbContext),
            new GenericRepository<UserAchievement>(dbContext),
            new GenericRepository<Notification>(dbContext));
        return new GamificationService(
            repos,
            new GamificationNotifiers(
                Substitute.For<IPushNotificationService>(),
                Substitute.For<IFriendFeedEventEmitter>()),
            Substitute.For<IUserDateService>(),
            new XpAwarder(new GenericRepository<XpAwardLog>(dbContext)),
            unitOfWork,
            Substitute.For<IFeatureFlagService>(),
            NullLogger<GamificationService>.Instance);
    }
}
