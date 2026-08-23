using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Backfill;
using Orbit.Application.Gamification.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Queries;

public record AchievementDto(
    string Id,
    string Name,
    string Description,
    string Category,
    string Rarity,
    int XpReward,
    string IconKey,
    bool IsEarned,
    DateTime? EarnedAtUtc,
    int? ProgressCurrent = null,
    int? ProgressTarget = null);

public record AchievementsResponse(IReadOnlyList<AchievementDto> Achievements);

public record GetAchievementsQuery(Guid UserId) : IRequest<Result<AchievementsResponse>>;

public class GetAchievementsQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<UserAchievement> achievementRepository,
    IFeatureFlagService featureFlagService,
    IAchievementEligibilityReconciliationService reconciliationService,
    IAchievementProgressService progressService,
    IProductAnalytics productAnalytics,
    ILogger<GetAchievementsQueryHandler> logger) : IRequestHandler<GetAchievementsQuery, Result<AchievementsResponse>>
{
    public async Task<Result<AchievementsResponse>> Handle(GetAchievementsQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<AchievementsResponse>(ErrorMessages.UserNotFound);

        if (!user.HasProAccess)
        {
            var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(
                request.UserId,
                cancellationToken);
            if (!enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier))
                return Result.PayGateFailure<AchievementsResponse>("Gamification is a Pro feature. Upgrade to unlock!");
        }

        await ReconcileHistoricalEligibilityOnFirstReadAsync(user, cancellationToken);

        var earnedList = await achievementRepository.FindAsync(a => a.UserId == request.UserId, cancellationToken);
        var earnedMap = earnedList.ToDictionary(a => a.AchievementId, a => a.EarnedAtUtc);
        var earnedIds = earnedMap.Keys.ToHashSet();

        var metrics = await progressService.LoadAsync(user, earnedIds, cancellationToken);

        var achievements = AchievementDefinitions.All
            .Where(def => !def.IsRetired || earnedMap.ContainsKey(def.Id))
            .Select(def =>
        {
            var isEarned = earnedMap.TryGetValue(def.Id, out var earnedAt);
            var (progressCurrent, progressTarget) = AchievementProgressCalculator.Compute(def, metrics, isEarned);
            return new AchievementDto(
                def.Id,
                def.Name,
                def.Description,
                def.Category.ToString(),
                def.Rarity.ToString(),
                def.XpReward,
                def.IconKey,
                isEarned,
                isEarned ? earnedAt : null,
                progressCurrent,
                progressTarget);
            }).ToList();

        AnalyticsCapture.SafeCaptureUserEvent(
            productAnalytics,
            logger,
            user,
            "achievements_viewed",
            new Dictionary<string, object>
            {
                ["isPro"] = user.HasProAccess,
                ["earnedCount"] = earnedList.Count
            });

        return Result.Success(new AchievementsResponse(achievements));
    }

    /// <summary>
    /// Runs a bounded lazy migration at most once for a legacy account, then the persisted stamp makes
    /// future reads write-free. Unlike the recurring recalculation removed from GET /streak in #331,
    /// this repairs one account's historical eligibility once and never takes a write lock per request.
    /// </summary>
    private async Task ReconcileHistoricalEligibilityOnFirstReadAsync(
        User user,
        CancellationToken cancellationToken)
    {
        if (user.AchievementEligibilityReconciledAtUtc is null)
            await reconciliationService.ReconcileUnlockedUserAsync(user, cancellationToken);
    }
}
