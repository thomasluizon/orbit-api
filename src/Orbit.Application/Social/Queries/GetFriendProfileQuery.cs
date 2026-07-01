using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Profile.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Queries;

/// <summary>
/// A friend's profile stats, returned only to an accepted friend. Unlike the public profile this
/// carries no visibility flags: friends see the full streak, level, and achievements. Reuses
/// <see cref="PublicAchievement"/> so both surfaces share the same achievement rendering on the client.
/// </summary>
public record FriendProfileView(
    Guid UserId,
    string Handle,
    string DisplayName,
    int CurrentStreak,
    int Level,
    IReadOnlyList<PublicAchievement> Achievements);

public record GetFriendProfileQuery(Guid UserId, Guid FriendUserId) : IRequest<Result<FriendProfileView>>;

public class GetFriendProfileQueryHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    IGenericRepository<User> userRepository,
    IGenericRepository<UserAchievement> achievementRepository) : IRequestHandler<GetFriendProfileQuery, Result<FriendProfileView>>
{
    public async Task<Result<FriendProfileView>> Handle(GetFriendProfileQuery request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<FriendProfileView>();

        if (!await friendGraphService.AreAcceptedFriendsAsync(request.UserId, request.FriendUserId, cancellationToken))
            return Result.Failure<FriendProfileView>(ErrorMessages.UserNotFound);

        var matches = await userRepository.FindAsync(u => u.Id == request.FriendUserId, cancellationToken);
        var friend = matches.FirstOrDefault();
        if (friend is null)
            return Result.Failure<FriendProfileView>(ErrorMessages.UserNotFound);

        var level = LevelDefinitions.GetLevelForXp(friend.TotalXp).Level;
        var achievements = await BuildAchievementsAsync(friend.Id, cancellationToken);

        return Result.Success(new FriendProfileView(
            friend.Id,
            friend.Handle ?? string.Empty,
            friend.Name,
            friend.CurrentStreak,
            level,
            achievements));
    }

    private async Task<IReadOnlyList<PublicAchievement>> BuildAchievementsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var earned = await achievementRepository.FindAsync(a => a.UserId == userId, cancellationToken);

        return earned
            .OrderByDescending(a => a.EarnedAtUtc)
            .Select(a => AchievementDefinitions.GetById(a.AchievementId))
            .Where(def => def is not null)
            .Select(def => new PublicAchievement(def!.Name, def.IconKey, def.Rarity.ToString()))
            .ToList();
    }
}
