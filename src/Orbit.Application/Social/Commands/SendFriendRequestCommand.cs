using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Commands;

public record SendFriendRequestCommand(Guid UserId, string? Handle, string? ReferralCode) : IRequest<Result<Guid>>;

public partial class SendFriendRequestCommandHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    IGenericRepository<Friendship> friendshipRepository,
    IGenericRepository<Notification> notificationRepository,
    IUnitOfWork unitOfWork,
    IPushNotificationService pushNotificationService,
    ILogger<SendFriendRequestCommandHandler> logger) : IRequestHandler<SendFriendRequestCommand, Result<Guid>>
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
        await notificationRepository.AddAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await PushTargetAsync(target, notification, cancellationToken);

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

    private async Task PushTargetAsync(User target, Notification notification, CancellationToken cancellationToken)
    {
        try
        {
            await pushNotificationService.SendToUserAsync(
                target.Id, notification.Title, notification.Body, notification.Url, cancellationToken);
        }
        catch (Exception ex)
        {
            LogPushNotificationFailed(logger, ex, target.Id);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Friend-request push failed for user {UserId}")]
    private static partial void LogPushNotificationFailed(ILogger logger, Exception ex, Guid userId);
}
