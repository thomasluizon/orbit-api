using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Backfill;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

public sealed class AchievementEligibilityReconciliationQueryTests
{
    [Fact]
    public async Task ReconcileAll_FullyReconciledFleet_UsesOneCandidateQuery()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var context = factory.Context;
        var user = User.Create("Reconciled User", "reconciled@example.com").Value;
        user.MarkAchievementEligibilityReconciled();
        context.Users.Add(user);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        counter.Reset();

        var featureFlagService = Substitute.For<IFeatureFlagService>();
        var gamificationService = Substitute.For<IGamificationService>();
        var service = new AchievementEligibilityReconciliationService(
            new GenericRepository<User>(context),
            new GenericRepository<Habit>(context),
            new GenericRepository<HabitLog>(context),
            new GenericRepository<Goal>(context),
            new GenericRepository<UserAchievement>(context),
            gamificationService,
            featureFlagService);

        var result = await service.ReconcileAllAsync();

        result.Should().Be(new AchievementEligibilityReconciliationResult(0, 0));
        counter.CommandCount.Should().Be(1);
        await featureFlagService.DidNotReceive().GetUserIdsWithEnabledKeyAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyCollection<User>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAll_GrantConcurrencyRetry_PersistsStampAndConverges()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var context = factory.Context;
        var user = User.Create("Legacy User", "legacy@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.CompleteOnboardingChecklist();
        context.Users.Add(user);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE \"Users\" SET \"AchievementEligibilityReconciledAtUtc\" = NULL WHERE \"Id\" = {0}",
            user.Id);
        context.ChangeTracker.Clear();

        var unitOfWork = Substitute.For<IUnitOfWork>();
        var saveAttempts = 0;
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                saveAttempts++;
                if (saveAttempts == 1)
                    throw new DbUpdateConcurrencyException("simulated stale user");

                return context.SaveChangesAsync(call.Arg<CancellationToken>());
            });
        unitOfWork.When(work => work.ResetTracking())
            .Do(_ => context.ChangeTracker.Clear());

        var userRepository = new GenericRepository<User>(context);
        var achievementRepository = new GenericRepository<UserAchievement>(context);
        var featureFlagService = Substitute.For<IFeatureFlagService>();
        featureFlagService.GetUserIdsWithEnabledKeyAsync(
                FeatureFlagKeys.GamificationFreeTier,
                Arg.Any<IReadOnlyCollection<User>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<User>>(1)
                .Select(candidate => candidate.Id)
                .ToHashSet());
        featureFlagService.GetEnabledKeysForUserAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.GamificationFreeTier]);

        var gamificationService = new GamificationService(
            new GamificationRepositories(
                userRepository,
                new GenericRepository<Habit>(context),
                new GenericRepository<HabitLog>(context),
                new GenericRepository<Goal>(context),
                achievementRepository,
                new GenericRepository<Notification>(context)),
            new GamificationNotifiers(
                Substitute.For<IPushNotificationService>(),
                Substitute.For<IFriendFeedEventEmitter>()),
            Substitute.For<IUserDateService>(),
            new XpAwarder(new GenericRepository<XpAwardLog>(context)),
            unitOfWork,
            featureFlagService,
            Substitute.For<ILogger<GamificationService>>());
        var service = new AchievementEligibilityReconciliationService(
            userRepository,
            new GenericRepository<Habit>(context),
            new GenericRepository<HabitLog>(context),
            new GenericRepository<Goal>(context),
            achievementRepository,
            gamificationService,
            featureFlagService);

        var first = await service.ReconcileAllAsync();
        context.ChangeTracker.Clear();
        var persistedUser = await context.Users.SingleAsync(candidate => candidate.Id == user.Id);
        persistedUser.AchievementEligibilityReconciledAtUtc.Should().NotBeNull();
        context.ChangeTracker.Clear();
        var second = await service.ReconcileAllAsync();

        first.Should().Be(new AchievementEligibilityReconciliationResult(1, 1));
        second.Should().Be(new AchievementEligibilityReconciliationResult(0, 0));
        (await context.UserAchievements.CountAsync()).Should().Be(1);
        (await context.XpAwardLogs.CountAsync()).Should().Be(1);
        unitOfWork.Received(1).ResetTracking();
    }

    [Fact]
    public async Task InlineReadAndBackgroundSweep_ConcurrentForSameUser_GrantAndAwardXpOnce()
    {
        var databaseName = $"AchievementEligibilityRace_{Guid.NewGuid()}";
        Guid userId;

        await using (var seed = CreateInMemoryContext(databaseName))
        {
            var user = User.Create("Legacy User", "legacy-race@example.com").Value;
            user.StartTrial(DateTime.UtcNow.AddDays(-1));
            user.CompleteOnboardingChecklist();
            typeof(User).GetProperty(nameof(User.AchievementEligibilityReconciledAtUtc))!
                .SetValue(user, null);
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            userId = user.Id;
        }

        var coordinator = new ConcurrentSaveCoordinator();
        await using var inlineContext = CreateInMemoryContext(
            databaseName,
            new CoordinatedSaveInterceptor(coordinator));
        await using var sweepContext = CreateInMemoryContext(
            databaseName,
            new CoordinatedSaveInterceptor(coordinator));
        var featureFlagService = Substitute.For<IFeatureFlagService>();
        featureFlagService.GetEnabledKeysForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns([FeatureFlagKeys.GamificationFreeTier]);
        featureFlagService.GetUserIdsWithEnabledKeyAsync(
                FeatureFlagKeys.GamificationFreeTier,
                Arg.Any<IReadOnlyCollection<User>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<User>>(1)
                .Select(user => user.Id)
                .ToHashSet());

        var inlineReconciliation = CreateReconciliationService(inlineContext, featureFlagService);
        var sweepReconciliation = CreateReconciliationService(sweepContext, featureFlagService);
        var progressService = Substitute.For<IAchievementProgressService>();
        progressService.LoadAsync(
                Arg.Any<User>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(AchievementProgressMetrics.Empty);
        var handler = new GetAchievementsQueryHandler(
            new GenericRepository<User>(inlineContext),
            new GenericRepository<UserAchievement>(inlineContext),
            featureFlagService,
            inlineReconciliation,
            progressService,
            Substitute.For<IProductAnalytics>(),
            Substitute.For<ILogger<GetAchievementsQueryHandler>>());

        var inlineTask = handler.Handle(new GetAchievementsQuery(userId), CancellationToken.None);
        var sweepTask = sweepReconciliation.ReconcileAllAsync();
        await Task.WhenAll(inlineTask, sweepTask);

        var inlineResult = await inlineTask;
        inlineResult.IsSuccess.Should().BeTrue();
        inlineResult.Value.Achievements.Single(
                achievement => achievement.Id == AchievementDefinitions.OnboardingComplete)
            .IsEarned.Should().BeTrue();
        coordinator.InitialSaveAttempts.Should().Be(2);

        await using var verify = CreateInMemoryContext(databaseName);
        var persistedUser = await verify.Users.SingleAsync(user => user.Id == userId);
        persistedUser.AchievementEligibilityReconciledAtUtc.Should().NotBeNull();
        persistedUser.TotalXp.Should().Be(
            AchievementDefinitions.GetById(AchievementDefinitions.OnboardingComplete)!.XpReward);
        (await verify.UserAchievements.CountAsync(achievement => achievement.UserId == userId))
            .Should().Be(1);
        (await verify.XpAwardLogs.CountAsync(log => log.UserId == userId))
            .Should().Be(1);
    }

    private static AchievementEligibilityReconciliationService CreateReconciliationService(
        OrbitDbContext context,
        IFeatureFlagService featureFlagService)
    {
        var userRepository = new GenericRepository<User>(context);
        var habitRepository = new GenericRepository<Habit>(context);
        var habitLogRepository = new GenericRepository<HabitLog>(context);
        var goalRepository = new GenericRepository<Goal>(context);
        var achievementRepository = new GenericRepository<UserAchievement>(context);
        var gamificationService = new GamificationService(
            new GamificationRepositories(
                userRepository,
                habitRepository,
                habitLogRepository,
                goalRepository,
                achievementRepository,
                new GenericRepository<Notification>(context)),
            new GamificationNotifiers(
                Substitute.For<IPushNotificationService>(),
                Substitute.For<IFriendFeedEventEmitter>()),
            Substitute.For<IUserDateService>(),
            new XpAwarder(new GenericRepository<XpAwardLog>(context)),
            new UnitOfWork(context, new DatabaseConnectionSettings()),
            featureFlagService,
            Substitute.For<ILogger<GamificationService>>());

        return new AchievementEligibilityReconciliationService(
            userRepository,
            habitRepository,
            habitLogRepository,
            goalRepository,
            achievementRepository,
            gamificationService,
            featureFlagService);
    }

    private static OrbitDbContext CreateInMemoryContext(
        string databaseName,
        ISaveChangesInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(databaseName);
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new OrbitDbContext(builder.Options);
    }

    private sealed class ConcurrentSaveCoordinator
    {
        private readonly TaskCompletionSource _bothSavesStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _winnerSaved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private DbContext? _winner;
        private int _initialSaveAttempts;

        internal int InitialSaveAttempts => _initialSaveAttempts;

        internal async Task BeforeSaveAsync(DbContext context)
        {
            var attempt = Interlocked.Increment(ref _initialSaveAttempts);
            if (attempt == 1)
            {
                _winner = context;
                await _bothSavesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return;
            }

            if (attempt == 2)
            {
                _bothSavesStarted.TrySetResult();
                await _winnerSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                throw new DbUpdateConcurrencyException("simulated concurrent reconciliation");
            }
        }

        internal void AfterSave(DbContext context)
        {
            if (ReferenceEquals(context, _winner))
                _winnerSaved.TrySetResult();
        }
    }

    private sealed class CoordinatedSaveInterceptor(ConcurrentSaveCoordinator coordinator)
        : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await coordinator.BeforeSaveAsync(eventData.Context!);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            coordinator.AfterSave(eventData.Context!);
            return ValueTask.FromResult(result);
        }
    }
}
