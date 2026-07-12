using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Caching;

public class GoalAiCacheInvalidationTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static string GoalReviewKey(string language) => $"goal-review:{UserId}:{language}";

    [Fact]
    public void InvalidateGoalReviewCache_RemovesEntryForEverySupportedLanguage()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        foreach (var language in AppConstants.SupportedLanguages)
            cache.Set(GoalReviewKey(language), "cached review");

        CacheInvalidationHelper.InvalidateGoalReviewCache(cache, UserId);

        foreach (var language in AppConstants.SupportedLanguages)
            cache.TryGetValue(GoalReviewKey(language), out _).Should().BeFalse();
    }

    [Fact]
    public void InvalidateUserAiCaches_AlsoClearsGoalReview()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(GoalReviewKey("en"), "cached review");

        CacheInvalidationHelper.InvalidateUserAiCaches(cache, UserId, new DateOnly(2026, 7, 12));

        cache.TryGetValue(GoalReviewKey("en"), out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateGoal_InvalidatesCachedGoalReview()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(GoalReviewKey("en"), "stale review");

        var goalRepo = Substitute.For<IGenericRepository<Goal>>();
        var payGate = Substitute.For<IPayGateService>();
        var userDateService = Substitute.For<IUserDateService>();
        var gamificationService = Substitute.For<IGamificationService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();

        payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 7, 12));

        var handler = new CreateGoalCommandHandler(
            goalRepo, payGate, userDateService, gamificationService, unitOfWork, cache,
            Substitute.For<ILogger<CreateGoalCommandHandler>>());

        var result = await handler.Handle(
            new CreateGoalCommand(UserId, "Run a marathon", null, 42.2m, "km", null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        cache.TryGetValue(GoalReviewKey("en"), out _).Should().BeFalse();
    }
}
