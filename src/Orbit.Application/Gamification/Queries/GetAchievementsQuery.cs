using MediatR;
using Orbit.Application.Common;
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
    DateTime? EarnedAtUtc);

public record AchievementsResponse(IReadOnlyList<AchievementDto> Achievements);

public record GetAchievementsQuery(Guid UserId) : IRequest<Result<AchievementsResponse>>;

public class GetAchievementsQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<UserAchievement> achievementRepository) : IRequestHandler<GetAchievementsQuery, Result<AchievementsResponse>>
{
    public async Task<Result<AchievementsResponse>> Handle(GetAchievementsQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<AchievementsResponse>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        if (!user.HasProAccess)
            return Result.PayGateFailure<AchievementsResponse>("Gamification is a Pro feature. Upgrade to unlock!");

        var earnedList = await achievementRepository.FindAsync(a => a.UserId == request.UserId, cancellationToken);
        var earnedMap = earnedList.ToDictionary(a => a.AchievementId, a => a.EarnedAtUtc);

        var achievements = AchievementDefinitions.All.Select(def =>
        {
            var isEarned = earnedMap.TryGetValue(def.Id, out var earnedAt);
            return new AchievementDto(
                def.Id,
                def.Name,
                def.Description,
                def.Category.ToString(),
                def.Rarity.ToString(),
                def.XpReward,
                def.IconKey,
                isEarned,
                isEarned ? earnedAt : null);
        }).ToList();

        return Result.Success(new AchievementsResponse(achievements));
    }
}
