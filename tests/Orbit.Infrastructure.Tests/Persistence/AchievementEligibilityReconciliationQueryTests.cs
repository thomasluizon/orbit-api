using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Backfill;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
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
}
