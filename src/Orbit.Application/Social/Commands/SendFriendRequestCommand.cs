using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Commands;

public record SendFriendRequestCommand(Guid UserId, string? Handle, string? ReferralCode) : IRequest<Result<Guid>>;

public class SendFriendRequestCommandHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    IGenericRepository<Friendship> friendshipRepository,
    SocialNotificationDispatcher notificationDispatcher,
    IUnitOfWork unitOfWork) : IRequestHandler<SendFriendRequestCommand, Result<Guid>>
{
    private const string RequestNotificationUrl = "/social?tab=friends";

    public async Task<Result<Guid>> Handle(SendFriendRequestCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<Guid>();
        var requester = access.Value;

        var target = await friendGraphService.ResolveTargetAsync(request.Handle, request.ReferralCode, cancellationToken);
        if (target is null || target.Id == request.UserId || !target.SocialOptIn)
            return Result.Failure<Guid>(ErrorMessages.UserNotFound);

        if (await friendGraphService.IsBlockedBetweenAsync(request.UserId, target.Id, cancellationToken))
            return Result.Failure<Guid>(ErrorMessages.UserNotFound);

        var existing = await friendGraphService.FindFriendshipAsync(request.UserId, target.Id, cancellationToken);
        if (existing is not null)
            return Result.Failure<Guid>(ErrorMessages.AlreadyFriends);

        var friendCount = await friendGraphService.CountAcceptedFriendsAsync(request.UserId, cancellationToken);
        if (friendCount >= AppConstants.MaxFriends)
            return Result.Failure<Guid>(ErrorMessages.FriendLimitReached.Format(AppConstants.MaxFriends));

        var createResult = Friendship.Create(request.UserId, target.Id);
        if (createResult.IsFailure)
            return createResult.PropagateError<Guid>();

        await friendshipRepository.AddAsync(createResult.Value, cancellationToken);

        var notification = BuildRequestNotification(target, requester);
        await notificationDispatcher.StageAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await notificationDispatcher.PushAsync(notification, cancellationToken);

        return Result.Success(createResult.Value.Id);
    }

    private static Notification BuildRequestNotification(User target, User requester)
    {
        var isPortuguese = LocaleHelper.IsPortuguese(target.Language);
        var title = isPortuguese ? "Novo pedido de amizade" : "New friend request";
        var body = isPortuguese
            ? $"{requester.Name} quer ser seu amigo."
            : $"{requester.Name} wants to be your friend.";

        return Notification.Create(target.Id, title, body, RequestNotificationUrl);
    }
}
