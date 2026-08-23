using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class GoalCompletionServiceTests
{
    private static readonly DateOnly Today = new(2026, 8, 23);

    [Fact]
    public async Task SaveCompletedGoal_AmbiguousCommitReplay_DoesNotAwardTwice()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var dbContext = factory.Context;
        var user = User.Create("Replay User", "replay@example.com").Value;
        var goal = Goal.Create(user.Id, "Replay-safe goal", 1, "session").Value;
        dbContext.AddRange(user, goal);
        await dbContext.SaveChangesAsync();

        goal.MarkCompleted().IsSuccess.Should().BeTrue();
        var gamification = Substitute.For<IGamificationService>();
        var unitOfWork = new AmbiguousCommitReplayUnitOfWork(CreateUnitOfWork(dbContext));
        var service = CreateCompletionService(dbContext, gamification, unitOfWork);

        await service.SaveCompletedGoalAsync(user.Id, goal.Id);

        var persistedGoal = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        persistedGoal.Status.Should().Be(GoalStatus.Completed);
        await gamification.Received(1).ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncDerivedGoals_SourceLogRemovedAfterBatchRead_SkipsStaleSnapshot()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var dbContext = factory.Context;
        var user = User.Create("Concurrent User", "concurrent@example.com").Value;
        var habit = CreateHabit(user.Id, "Exercise");
        var goal = Goal.Create(user.Id, "Exercise once", 1, "session").Value;
        goal.AddHabit(habit);
        habit.Log(Today);
        dbContext.AddRange(user, habit, goal);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var gamification = Substitute.For<IGamificationService>();
        var unitOfWork = new SourceLogRemovalUnitOfWork(
            CreateUnitOfWork(dbContext),
            dbContext,
            user.Id,
            goal.Id);
        var service = CreateCompletionService(dbContext, gamification, unitOfWork);

        var updates = await service.SyncDerivedGoalsAsync(
            user.Id,
            [goal.Id],
            Today,
            passiveSync: true);

        updates.Should().BeEmpty();
        var persistedGoal = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        persistedGoal.Status.Should().Be(GoalStatus.Active);
        persistedGoal.CurrentValue.Should().Be(0);
        await gamification.DidNotReceive().ProcessGoalCompleted(
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkGoalsToHabit_TwoGoalsComplete_PersistsEachBeforeItsAward()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var dbContext = factory.Context;
        var user = User.Create("Link User", "link-goals@example.com").Value;
        var habit = CreateHabit(user.Id, "Exercise");
        var firstGoal = Goal.Create(user.Id, "First goal", 1, "session").Value;
        var secondGoal = Goal.Create(user.Id, "Second goal", 1, "session").Value;
        habit.Log(Today);
        dbContext.AddRange(user, habit, firstGoal, secondGoal);
        await dbContext.SaveChangesAsync();

        var awardCounts = new List<int>();
        var gamification = Substitute.For<IGamificationService>();
        gamification.ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                awardCounts.Add(await dbContext.Goals.CountAsync(g => g.Status == GoalStatus.Completed));
            });
        var unitOfWork = CreateUnitOfWork(dbContext);
        var handler = new LinkGoalsToHabitCommandHandler(
            new GenericRepository<Habit>(dbContext),
            new GenericRepository<Goal>(dbContext),
            SuccessfulPayGate(),
            CreateCompletionService(dbContext, gamification, unitOfWork),
            StubToday(user.Id));

        var result = await handler.Handle(
            new LinkGoalsToHabitCommand(user.Id, habit.Id, [firstGoal.Id, secondGoal.Id]),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var goals = await dbContext.Goals.AsNoTracking().OrderBy(g => g.Title).ToListAsync();
        goals.Should().OnlyContain(g => g.Status == GoalStatus.Completed);
        awardCounts.Should().Equal(1, 2);
        await gamification.Received(2).ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkHabitsToGoal_AwardFailure_RollsBackAndRetryCompletes()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var dbContext = factory.Context;
        var user = User.Create("Retry User", "link-habits@example.com").Value;
        var habit = CreateHabit(user.Id, "Read");
        var goal = Goal.Create(user.Id, "Read once", 1, "session").Value;
        habit.Log(Today);
        dbContext.AddRange(user, habit, goal);
        await dbContext.SaveChangesAsync();

        var attempts = 0;
        var gamification = Substitute.For<IGamificationService>();
        gamification.ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>())
            .Returns(_ => ++attempts == 1
                ? Task.FromException(new InvalidOperationException("Forced award failure"))
                : Task.CompletedTask);
        var unitOfWork = CreateUnitOfWork(dbContext);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new LinkHabitsToGoalCommandHandler(
            new GenericRepository<Goal>(dbContext),
            new GenericRepository<Habit>(dbContext),
            SuccessfulPayGate(),
            CreateCompletionService(dbContext, gamification, unitOfWork),
            StubToday(user.Id),
            cache);
        var command = new LinkHabitsToGoalCommand(user.Id, goal.Id, [habit.Id]);

        var firstAttempt = () => handler.Handle(command, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();

        var afterFailure = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        afterFailure.Status.Should().Be(GoalStatus.Active);
        afterFailure.CurrentValue.Should().Be(0);
        (await dbContext.Entry(afterFailure).Collection(g => g.Habits).Query().CountAsync()).Should().Be(0);

        var retry = await handler.Handle(command, CancellationToken.None);

        retry.IsSuccess.Should().BeTrue();
        var afterRetry = await dbContext.Goals.AsNoTracking().SingleAsync(g => g.Id == goal.Id);
        afterRetry.Status.Should().Be(GoalStatus.Completed);
        afterRetry.CurrentValue.Should().Be(1);
        await gamification.Received(2).ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncDerivedGoals_ConcurrencyRetryResetsTrackingMidLoop_CompletesEveryCandidate()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var dbContext = factory.Context;
        var user = User.Create("Reset User", "reset@example.com").Value;
        var habit = CreateHabit(user.Id, "Train");
        var goals = Enumerable.Range(1, 3)
            .Select(index => Goal.Create(user.Id, $"Goal {index}", 1, "session").Value)
            .ToList();
        foreach (var goal in goals)
            goal.AddHabit(habit);
        habit.Log(Today);
        dbContext.Add(user);
        dbContext.Add(habit);
        dbContext.AddRange(goals);
        await dbContext.SaveChangesAsync();

        var unitOfWork = CreateUnitOfWork(dbContext);
        var retryUnitOfWork = Substitute.For<IUnitOfWork>();
        var saveAttempts = 0;
        retryUnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ++saveAttempts == 1
                ? Task.FromException<int>(new DbUpdateConcurrencyException("Forced retry"))
                : Task.FromResult(0));
        retryUnitOfWork.When(x => x.ResetTracking()).Do(_ => unitOfWork.ResetTracking());

        var awardAttempts = 0;
        var gamification = Substitute.For<IGamificationService>();
        gamification.ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (++awardAttempts == 1)
                {
                    await ConcurrencyRetry.SaveWithRetryAsync(
                        retryUnitOfWork,
                        _ => Task.CompletedTask,
                        call.ArgAt<CancellationToken>(1),
                        maxAttempts: 2);
                }
            });
        var service = CreateCompletionService(dbContext, gamification, unitOfWork);

        var updates = await service.SyncDerivedGoalsAsync(
            user.Id,
            goals.Select(g => g.Id).ToList(),
            Today);

        updates.Should().HaveCount(3).And.OnlyContain(update => update.JustCompleted);
        var persistedGoals = await dbContext.Goals.AsNoTracking().ToListAsync();
        persistedGoals.Should().OnlyContain(g => g.Status == GoalStatus.Completed);
        retryUnitOfWork.Received(1).ResetTracking();
        await gamification.Received(3).ProcessGoalCompleted(user.Id, Arg.Any<CancellationToken>());
    }

    private static Habit CreateHabit(Guid userId, string title)
    {
        var habit = Habit.Create(new HabitCreateParams(
            userId,
            title,
            FrequencyUnit.Day,
            1,
            DueDate: Today,
            IsFlexible: true)).Value;
        return habit;
    }

    private static IGoalCompletionService CreateCompletionService(
        OrbitDbContext dbContext,
        IGamificationService gamification,
        IUnitOfWork unitOfWork) =>
        new GoalCompletionService(new GenericRepository<Goal>(dbContext), gamification, unitOfWork);

    private static IUnitOfWork CreateUnitOfWork(OrbitDbContext dbContext) =>
        new UnitOfWork(dbContext, new DatabaseConnectionSettings());

    private static IPayGateService SuccessfulPayGate()
    {
        var payGate = Substitute.For<IPayGateService>();
        payGate.CanLinkGoalsToHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        return payGate;
    }

    private static IUserDateService StubToday(Guid userId)
    {
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(Today);
        return userDateService;
    }

    private sealed class AmbiguousCommitReplayUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            inner.SaveChangesAsync(cancellationToken);

        public async Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            await inner.ExecuteInTransactionAsync(operation, cancellationToken);
            inner.ResetTracking();
            await inner.ExecuteInTransactionAsync(operation, cancellationToken);
        }

        public Task<T> ExecuteInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            inner.ExecuteInTransactionAsync(operation, cancellationToken);

        public Task AcquireAdvisoryLockAsync(string key, CancellationToken cancellationToken = default) =>
            inner.AcquireAdvisoryLockAsync(key, cancellationToken);

        public void DiscardChanges() => inner.DiscardChanges();

        public void ResetTracking() => inner.ResetTracking();

        public void Dispose() => inner.Dispose();
    }

    private sealed class SourceLogRemovalUnitOfWork(
        IUnitOfWork inner,
        OrbitDbContext dbContext,
        Guid userId,
        Guid goalId) : IUnitOfWork
    {
        private bool _sourceRemoved;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            inner.SaveChangesAsync(cancellationToken);

        public Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            inner.ExecuteInTransactionAsync(operation, cancellationToken);

        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            if (!_sourceRemoved)
            {
                _sourceRemoved = true;
                var goal = await dbContext.Goals
                    .Include(candidate => candidate.Habits)
                    .ThenInclude(habit => habit.Logs)
                    .SingleAsync(
                        candidate => candidate.Id == goalId && candidate.UserId == userId,
                        cancellationToken);
                var habit = goal.Habits.Single();
                habit.Unlog(Today).IsSuccess.Should().BeTrue();
                GoalProgressSyncService.SyncCurrentProgress(goal, Today).Synced.Should().BeTrue();
                await dbContext.SaveChangesAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
            }

            return await inner.ExecuteInTransactionAsync(operation, cancellationToken);
        }

        public Task AcquireAdvisoryLockAsync(string key, CancellationToken cancellationToken = default) =>
            inner.AcquireAdvisoryLockAsync(key, cancellationToken);

        public void DiscardChanges() => inner.DiscardChanges();

        public void ResetTracking() => inner.ResetTracking();

        public void Dispose() => inner.Dispose();
    }
}
