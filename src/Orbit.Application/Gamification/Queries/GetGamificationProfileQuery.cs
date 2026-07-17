using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Queries;

public record GamificationProfileResponse(
    int TotalXp,
    int Level,
    string LevelTitle,
    int XpForCurrentLevel,
    int XpForNextLevel,
    int? XpToNextLevel,
    int AchievementsEarned,
    int AchievementsTotal,
    IReadOnlyList<AchievementDto> Achievements,
    IReadOnlyList<UserAchievementDto> UserAchievements,
    int CurrentStreak,
    int LongestStreak,
    DateOnly? LastActiveDate,
    bool IsPro,
    bool AchievementsLocked,
    NextRewardCarrot NextReward);

public record UserAchievementDto(string AchievementId, DateTime EarnedAtUtc);

public record GamificationProTeaser(string Kind, bool Locked);

public record NextRewardCarrot(
    int NextLevel,
    string NextLevelTitle,
    int XpToNextLevel,
    GamificationProTeaser? ProTeaser);

public record GetGamificationProfileQuery(Guid UserId) : IRequest<Result<GamificationProfileResponse>>;

public class GetGamificationProfileQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<UserAchievement> achievementRepository,
    IFeatureFlagService featureFlagService,
    IAchievementProgressService progressService) : IRequestHandler<GetGamificationProfileQuery, Result<GamificationProfileResponse>>
{
    public async Task<Result<GamificationProfileResponse>> Handle(GetGamificationProfileQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<GamificationProfileResponse>(ErrorMessages.UserNotFound);

        var enabledFlags = await featureFlagService.GetEnabledKeysForUserAsync(request.UserId, cancellationToken);
        var unlocked = user.HasProAccess || enabledFlags.Contains(FeatureFlagKeys.GamificationFreeTier);
        if (!unlocked)
            return Result.PayGateFailure<GamificationProfileResponse>("Gamification is a Pro feature. Upgrade to unlock!");

        var currentLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
        var xpToNext = LevelDefinitions.GetXpToNextLevel(user.TotalXp);
        var nextLevelNumber = currentLevel.Level + 1;
        var nextLevelXpRequired = LevelDefinitions.XpRequiredForLevel(nextLevelNumber);

        var achievementsLocked = !user.HasProAccess;
        var (achievements, userAchievements, achievementsEarned) =
            achievementsLocked
                ? (new List<AchievementDto>(), new List<UserAchievementDto>(), 0)
                : await BuildAchievementsAsync(user, cancellationToken);

        var proTeaser = user.HasProAccess
            ? null
            : new GamificationProTeaser("achievements", true);
        var nextReward = new NextRewardCarrot(
            nextLevelNumber,
            LevelDefinitions.TitleForLevel(nextLevelNumber),
            xpToNext,
            proTeaser);

        return Result.Success(new GamificationProfileResponse(
            user.TotalXp,
            currentLevel.Level,
            currentLevel.Title,
            currentLevel.XpRequired,
            nextLevelXpRequired,
            xpToNext,
            achievementsEarned,
            AchievementDefinitions.All.Count,
            achievements,
            userAchievements,
            user.CurrentStreak,
            user.LongestStreak,
            user.LastActiveDate,
            user.HasProAccess,
            achievementsLocked,
            nextReward));
    }

    private async Task<(List<AchievementDto> Achievements, List<UserAchievementDto> UserAchievements, int EarnedCount)> BuildAchievementsAsync(
        User user, CancellationToken cancellationToken)
    {
        var earned = await achievementRepository.FindAsync(a => a.UserId == user.Id, cancellationToken);
        var earnedMap = earned.ToDictionary(a => a.AchievementId, a => a.EarnedAtUtc);
        var earnedIds = earnedMap.Keys.ToHashSet();

        var metrics = await progressService.LoadAsync(user, earnedIds, cancellationToken);

        var achievements = AchievementDefinitions.All.Select(def =>
        {
            var isEarned = earnedMap.TryGetValue(def.Id, out var earnedAt);
            var (progressCurrent, progressTarget) = AchievementProgressCalculator.Compute(def, metrics, isEarned);
            return new AchievementDto(
                def.Id, def.Name, def.Description,
                def.Category.ToString(), def.Rarity.ToString(),
                def.XpReward, def.IconKey, isEarned, isEarned ? earnedAt : null,
                progressCurrent, progressTarget);
        }).ToList();

        var userAchievements = earned.Select(e =>
            new UserAchievementDto(e.AchievementId, e.EarnedAtUtc)).ToList();

        return (achievements, userAchievements, earned.Count);
    }
}
