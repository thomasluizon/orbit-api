using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Commands;

public record AcceptFriendRequestCommand(Guid UserId, Guid FriendshipId) : IRequest<Result>;

public partial class AcceptFriendRequestCommandHandler(
    SocialAccessGuard socialAccessGuard,
    IGenericRepository<Friendship> friendshipRepository,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IPushNotificationService pushNotificationService,
    ILogger<AcceptFriendRequestCommandHandler> logger) : IRequestHandler<AcceptFriendRequestCommand, Result>
{
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

        await NotifyRequesterAsync(friendship.RequesterId, access.Value, cancellationToken);

        return Result.Success();
    }

    private async Task NotifyRequesterAsync(Guid requesterId, User accepter, CancellationToken cancellationToken)
    {
        var requester = await userRepository.FindOneTrackedAsync(
            u => u.Id == requesterId,
            cancellationToken: cancellationToken);

        if (requester is null || !requester.SocialOptIn)
            return;

        var isPortuguese = LocaleHelper.IsPortuguese(requester.Language);
        var title = isPortuguese ? "Pedido de amizade aceito" : "Friend request accepted";
        var body = isPortuguese
            ? $"{accepter.Name} aceitou seu pedido de amizade."
            : $"{accepter.Name} accepted your friend request.";

        try
        {
            await pushNotificationService.SendToUserAsync(requesterId, title, body, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            LogPushNotificationFailed(logger, ex, requesterId);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Friend-accepted push failed for user {UserId}")]
    private static partial void LogPushNotificationFailed(ILogger logger, Exception ex, Guid userId);
}
