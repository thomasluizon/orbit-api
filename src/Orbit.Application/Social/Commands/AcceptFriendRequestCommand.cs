using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Commands;

public record AcceptFriendRequestCommand(Guid UserId, Guid FriendshipId) : IRequest<Result>;

public class AcceptFriendRequestCommandHandler(
    SocialAccessGuard socialAccessGuard,
    IGenericRepository<Friendship> friendshipRepository,
    IGenericRepository<User> userRepository,
    IGenericRepository<UserAchievement> achievementRepository,
    SocialNotificationDispatcher notificationDispatcher,
    IXpAwarder xpAwarder,
    IUnitOfWork unitOfWork) : IRequestHandler<AcceptFriendRequestCommand, Result>
{
    private const string FirstFriendAchievementId = "first_friend";
    private const string SquadGoalsAchievementId = "squad_goals";
    private const int SquadGoalsThreshold = 5;
    private const string FriendNotificationUrl = "/social?tab=friends";

    public async Task<Result> Handle(AcceptFriendRequestCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError();

        var friendship = await friendshipRepository.FindOneTrackedAsync(
            f => f.Id == request.FriendshipId && f.AddresseeId == request.UserId,
            cancellationToken: cancellationToken);

        if (friendship is null)
            return Result.Failure(ErrorMessages.FriendRequestNotFound);

        var acceptResult = friendship.Accept();
        if (acceptResult.IsFailure)
            return acceptResult;

        await unitOfWork.SaveChangesAsync(cancellationToken);

        var accepter = access.Value;
        var requester = await userRepository.FindOneTrackedAsync(
            u => u.Id == friendship.RequesterId,
            cancellationToken: cancellationToken);

        await AwardFriendCountAchievementsAsync(accepter, cancellationToken);
        if (requester is not null)
            await AwardFriendCountAchievementsAsync(requester, cancellationToken);

        var notification = BuildRequesterNotification(requester, accepter);
        if (notification is not null)
            await notificationDispatcher.StageAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (notification is not null)
            await notificationDispatcher.PushAsync(notification, cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// Awards the friend-count achievements to one participant after a friendship is accepted: "First
    /// Friend" on the first accepted friendship and "Squad Goals" at <see cref="SquadGoalsThreshold"/>
    /// accepted friendships. Both participants gain a friend on accept, so the caller invokes this for
    /// each. The conventional achievement ids stay dormant — <see cref="AchievementChecks.TryGrant"/>
    /// no-ops on an unknown id — until the definitions ship, then this lights up automatically.
    /// See https://github.com/thomasluizon/orbit-ui-mobile/issues/196.
    /// </summary>
    private async Task AwardFriendCountAchievementsAsync(User user, CancellationToken cancellationToken)
    {
        var acceptedFriendCount = await friendshipRepository.CountAsync(
            f => (f.RequesterId == user.Id || f.AddresseeId == user.Id) && f.Status == FriendshipStatus.Accepted,
            cancellationToken);
        if (acceptedFriendCount < 1)
            return;

        var candidateIds = acceptedFriendCount >= SquadGoalsThreshold
            ? new[] { FirstFriendAchievementId, SquadGoalsAchievementId }
            : new[] { FirstFriendAchievementId };

        var alreadyEarned = await achievementRepository.FindAsync(
            a => a.UserId == user.Id && candidateIds.Contains(a.AchievementId),
            cancellationToken);
        var earned = alreadyEarned.Select(a => a.AchievementId).ToHashSet();
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();

        foreach (var achievementId in candidateIds)
            AchievementChecks.TryGrant(achievementId, user, earned, newAchievements);

        if (newAchievements.Count == 0)
            return;

        foreach (var (entity, definition) in newAchievements)
        {
            await achievementRepository.AddAsync(entity, cancellationToken);
            await xpAwarder.AwardAsync(
                user, definition.XpReward, XpAwardSource.Achievement, entity.Id,
                awardedAtUtc: DateTime.UtcNow, cancellationToken);
        }

        LevelDefinitions.SyncLevel(user);
    }

    private static Notification? BuildRequesterNotification(User? requester, User accepter)
    {
        if (requester is null || !requester.SocialOptIn)
            return null;

        var isPortuguese = LocaleHelper.IsPortuguese(requester.Language);
        var title = isPortuguese ? "Pedido de amizade aceito" : "Friend request accepted";
        var body = isPortuguese
            ? $"{accepter.Name} aceitou seu pedido de amizade."
            : $"{accepter.Name} accepted your friend request.";

        return Notification.Create(requester.Id, title, body, FriendNotificationUrl);
    }
}
