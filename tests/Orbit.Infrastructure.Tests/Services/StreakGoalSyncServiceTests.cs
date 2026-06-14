using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// DB-backed regression for the passive streak-goal sweep: an active streak goal advances its
/// CurrentValue from linked habit logs without any request, auto-completes when it reaches target,
/// and routes that completion through gamification exactly once.
/// </summary>
public class StreakGoalSyncServiceTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task SyncActiveStreakGoals_AdvancesCurrentValueFromLinkedHabitLogs()
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
        await service.SyncActiveStreakGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.CurrentValue.Should().Be(3);
        reloaded.Status.Should().Be(GoalStatus.Active);
        reloaded.StreakSyncedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task SyncActiveStreakGoals_StreakReachesTarget_AutoCompletesAndGamifiesOnce()
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
        await service.SyncActiveStreakGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.Status.Should().Be(GoalStatus.Completed);
        reloaded.CompletedAtUtc.Should().NotBeNull();
        await gamification.Received(1).ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncActiveStreakGoals_AlreadySyncedToday_LeavesValueAndSkipsGamification()
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
        await service.SyncActiveStreakGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.CurrentValue.Should().Be(3);
        await gamification.DidNotReceive().ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncActiveStreakGoals_MultipleCompletingGoals_PersistsEachAndGamifiesEachUser()
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
        await service.SyncActiveStreakGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().ToListAsync();
        reloaded.Should().OnlyContain(g => g.Status == GoalStatus.Completed);
        await gamification.Received(1).ProcessGoalCompleted(firstUser.Id, Arg.Any<CancellationToken>());
        await gamification.Received(1).ProcessGoalCompleted(secondUser.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncActiveStreakGoals_NoLinkedHabits_LeavesGoalUntouched()
    {
        await using var dbContext = CreateInMemoryDbContext();
        var user = User.Create("Thomas", "thomas@test.com").Value;
        var goal = CreateStreakGoal(user.Id, target: 7);

        dbContext.Users.Add(user);
        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext, Substitute.For<IGamificationService>());
        await service.SyncActiveStreakGoals(CancellationToken.None);

        var reloaded = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        reloaded.CurrentValue.Should().Be(0);
        reloaded.StreakSyncedAtUtc.Should().BeNull();
    }

    private static Habit CreateDailyHabitLoggedLastDays(Guid userId, int days)
    {
        var habit = Habit.Create(new HabitCreateParams(
            userId, "Meditate", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        SetCreatedAtUtc(habit, Today.AddDays(-(days - 1)));
        for (var offset = days - 1; offset >= 0; offset--)
            habit.Log(Today.AddDays(-offset), advanceDueDate: false);
        return habit;
    }

    private static Goal CreateStreakGoal(Guid userId, decimal target) =>
        Goal.Create(new Goal.CreateGoalParams(
            userId, "Daily streak", target, "days", Type: GoalType.Streak)).Value;

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

    private static StreakGoalSyncService CreateService(OrbitDbContext dbContext, IGamificationService gamificationService)
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(gamificationService)
            .BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new StreakGoalSyncService(
            scopeFactory,
            NullLogger<StreakGoalSyncService>.Instance,
            new ConfigurationBuilder().Build());
    }
}
