using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Accountability.Services;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Accountability.Commands;

public record InviteAccountabilityBuddyCommand(
    Guid UserId,
    Guid BuddyUserId,
    AccountabilityCadence Cadence,
    IReadOnlyList<Guid> HabitIds) : IRequest<Result<Guid>>;

public partial class InviteAccountabilityBuddyCommandHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    AccountabilityPairService accountabilityPairService,
    AccountabilityRepositories repositories,
    IGenericRepository<Notification> notificationRepository,
    IPushNotificationService pushNotificationService,
    IUnitOfWork unitOfWork,
    ILogger<InviteAccountabilityBuddyCommandHandler> logger) : IRequestHandler<InviteAccountabilityBuddyCommand, Result<Guid>>
{
    private const string BuddyNotificationUrl = "/social?tab=buddies";

    public async Task<Result<Guid>> Handle(InviteAccountabilityBuddyCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<Guid>();
        var requester = access.Value;

        var buddy = await repositories.Users.FindOneTrackedAsync(
            u => u.Id == request.BuddyUserId,
            cancellationToken: cancellationToken);
        if (buddy is null || !buddy.SocialOptIn)
            return Result.Failure<Guid>(ErrorMessages.NotFriends);

        if (!await friendGraphService.AreAcceptedFriendsAsync(request.UserId, request.BuddyUserId, cancellationToken))
            return Result.Failure<Guid>(ErrorMessages.NotFriends);

        if (await friendGraphService.IsBlockedBetweenAsync(request.UserId, request.BuddyUserId, cancellationToken))
            return Result.Failure<Guid>(ErrorMessages.Blocked);

        var existingPair = await accountabilityPairService.FindActivePairBetweenAsync(
            request.UserId, request.BuddyUserId, cancellationToken);
        if (existingPair is not null)
            return Result.Failure<Guid>(ErrorMessages.AlreadyPaired);

        var activePairCount = await accountabilityPairService.CountActivePairsAsync(request.UserId, cancellationToken);
        if (activePairCount >= AppConstants.MaxAccountabilityPairs)
            return Result.Failure<Guid>(ErrorMessages.PairLimitReached.Format(AppConstants.MaxAccountabilityPairs));

        var createResult = AccountabilityPair.Create(request.UserId, request.BuddyUserId, request.Cadence);
        if (createResult.IsFailure)
            return createResult.PropagateError<Guid>();
        var pair = createResult.Value;

        await repositories.Pairs.AddAsync(pair, cancellationToken);

        var linkResult = await accountabilityPairService.ReplaceLinkedHabitsAsync(
            pair, request.UserId, request.HabitIds, cancellationToken);
        if (linkResult.IsFailure)
            return linkResult.PropagateError<Guid>();

        var notification = BuildBuddyNotification(buddy, requester);
        await notificationRepository.AddAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await PushBuddyAsync(buddy, notification, cancellationToken);

        return Result.Success(pair.Id);
    }

    private static Notification BuildBuddyNotification(User buddy, User requester)
    {
        var isPortuguese = LocaleHelper.IsPortuguese(buddy.Language);
        var title = isPortuguese ? "Novo convite de parceria" : "New accountability invite";
        var body = isPortuguese
            ? $"{requester.Name} quer ser seu parceiro de responsabilidade."
            : $"{requester.Name} wants to be your accountability buddy.";

        return Notification.Create(buddy.Id, title, body, BuddyNotificationUrl);
    }

    private async Task PushBuddyAsync(User buddy, Notification notification, CancellationToken cancellationToken)
    {
        try
        {
            await pushNotificationService.SendToUserAsync(
                buddy.Id, notification.Title, notification.Body, notification.Url, cancellationToken);
        }
        catch (Exception ex)
        {
            LogPushNotificationFailed(logger, ex, buddy.Id);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Accountability invite push failed for user {UserId}")]
    private static partial void LogPushNotificationFailed(ILogger logger, Exception ex, Guid userId);
}
