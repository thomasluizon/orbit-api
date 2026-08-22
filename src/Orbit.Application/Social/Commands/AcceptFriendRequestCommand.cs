using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Commands;

public record AcceptFriendRequestCommand(Guid UserId, Guid FriendshipId) : IRequest<Result>;

public class AcceptFriendRequestCommandHandler(
    SocialAccessGuard socialAccessGuard,
    IGenericRepository<Friendship> friendshipRepository,
    IGenericRepository<User> userRepository,
    SocialNotificationDispatcher notificationDispatcher,
    IUnitOfWork unitOfWork) : IRequestHandler<AcceptFriendRequestCommand, Result>
{
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

        var notification = BuildRequesterNotification(requester, accepter);
        if (notification is not null)
            await notificationDispatcher.StageAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (notification is not null)
            await notificationDispatcher.PushAsync(notification, cancellationToken);

        return Result.Success();
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
