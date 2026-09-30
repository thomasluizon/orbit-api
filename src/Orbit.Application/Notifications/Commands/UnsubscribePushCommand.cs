using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Commands;

public record UnsubscribePushCommand(
    Guid UserId,
    string Endpoint,
    string? P256dh = null,
    string? Auth = null,
    bool ReleaseOtherAccount = false) : IRequest<Result>;

public class UnsubscribePushCommandHandler(
    IGenericRepository<PushSubscription> pushSubscriptionRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UnsubscribePushCommand, Result>
{
    public async Task<Result> Handle(UnsubscribePushCommand request, CancellationToken cancellationToken)
    {
        var subscription = await pushSubscriptionRepository.FindOneTrackedAsync(
            s => s.Endpoint == request.Endpoint,
            cancellationToken: cancellationToken);

        if (subscription is not null && CanRelease(subscription, request))
        {
            pushSubscriptionRepository.Remove(subscription);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    /// <summary>
    /// Ordinary sign-out releases only the caller's row, so a delayed request cannot remove a
    /// subsequent account's claim. Device cleanup under another account must explicitly opt in
    /// and present the device's credentials.
    /// </summary>
    private static bool CanRelease(PushSubscription subscription, UnsubscribePushCommand request) =>
        subscription.UserId == request.UserId
        || (request.ReleaseOtherAccount
            && request.P256dh is not null
            && request.Auth is not null
            && subscription.MatchesCredentials(request.P256dh, request.Auth));
}
