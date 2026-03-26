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
    int? XpToNextLevel,
    int AchievementsEarned,
    int AchievementsTotal);

public record GetGamificationProfileQuery(Guid UserId) : IRequest<Result<GamificationProfileResponse>>;

public class GetGamificationProfileQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<UserAchievement> achievementRepository) : IRequestHandler<GetGamificationProfileQuery, Result<GamificationProfileResponse>>
{
    public async Task<Result<GamificationProfileResponse>> Handle(GetGamificationProfileQuery request, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, ct);
        if (user is null)
            return Result.Failure<GamificationProfileResponse>(ErrorMessages.UserNotFound);

        if (!user.HasProAccess)
            return Result.PayGateFailure<GamificationProfileResponse>("Gamification is a Pro feature. Upgrade to unlock!");

        var level = LevelDefinitions.GetLevelForXp(user.TotalXp);
        var xpToNext = LevelDefinitions.GetXpToNextLevel(user.TotalXp);
        var earned = await achievementRepository.FindAsync(a => a.UserId == request.UserId, ct);

        return Result.Success(new GamificationProfileResponse(
            user.TotalXp,
            level.Level,
            level.Title,
            xpToNext,
            earned.Count,
            AchievementDefinitions.All.Count));
    }
}
