using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification.Backfill;
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
            featureFlagService,
            new UnitOfWork(context, new DatabaseConnectionSettings()));

        var result = await service.ReconcileAllAsync();

        result.Should().Be(new AchievementEligibilityReconciliationResult(0, 0));
        counter.CommandCount.Should().Be(1);
        await featureFlagService.DidNotReceive().GetUserIdsWithEnabledKeyAsync(
            Arg.Any<string>(),
            Arg.Any<IReadOnlyCollection<User>>(),
            Arg.Any<CancellationToken>());
    }
}
