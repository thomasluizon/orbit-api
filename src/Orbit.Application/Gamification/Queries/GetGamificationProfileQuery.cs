using MediatR;
using Orbit.Application.Common;
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
    DateOnly? LastActiveDate);

public record UserAchievementDto(string AchievementId, DateTime EarnedAtUtc);

public record GetGamificationProfileQuery(Guid UserId) : IRequest<Result<GamificationProfileResponse>>;

public class GetGamificationProfileQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<UserAchievement> achievementRepository) : IRequestHandler<GetGamificationProfileQuery, Result<GamificationProfileResponse>>
{
    public async Task<Result<GamificationProfileResponse>> Handle(GetGamificationProfileQuery request, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, ct);
        if (user is null)
            return Result.Failure<GamificationProfileResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        if (!user.HasProAccess)
            return Result.PayGateFailure<GamificationProfileResponse>("Gamification is a Pro feature. Upgrade to unlock!");

        var currentLevel = LevelDefinitions.GetLevelForXp(user.TotalXp);
        var xpToNext = LevelDefinitions.GetXpToNextLevel(user.TotalXp);
        var nextLevel = currentLevel.Level < 10
            ? LevelDefinitions.All[currentLevel.Level]
            : currentLevel;
        var earned = await achievementRepository.FindAsync(a => a.UserId == request.UserId, ct);
        var earnedMap = earned.ToDictionary(a => a.AchievementId, a => a.EarnedAtUtc);

        var achievements = AchievementDefinitions.All.Select(def =>
        {
            var isEarned = earnedMap.TryGetValue(def.Id, out var earnedAt);
            return new AchievementDto(
                def.Id, def.Name, def.Description,
                def.Category.ToString(), def.Rarity.ToString(),
                def.XpReward, def.IconKey, isEarned, isEarned ? earnedAt : null);
        }).ToList();

        var userAchievements = earned.Select(e =>
            new UserAchievementDto(e.AchievementId, e.EarnedAtUtc)).ToList();

        return Result.Success(new GamificationProfileResponse(
            user.TotalXp,
            currentLevel.Level,
            currentLevel.Title,
            currentLevel.XpRequired,
            nextLevel.XpRequired,
            xpToNext,
            earned.Count,
            AchievementDefinitions.All.Count,
            achievements,
            userAchievements,
            user.CurrentStreak,
            user.LongestStreak,
            user.LastActiveDate));
    }
}
