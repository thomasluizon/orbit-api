using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Commands;

public record UnsubscribePushCommand(
    Guid UserId,
    string Endpoint,
    string? P256dh,
    string? Auth) : IRequest<Result>;

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
    /// The owner can always release its own row. Another account signed in on the same device can
    /// release it only by presenting the device's credentials, so a device left registered to a
    /// previous account stops counting there once the device turns push off or signs out.
    /// </summary>
    private static bool CanRelease(PushSubscription subscription, UnsubscribePushCommand request) =>
        subscription.UserId == request.UserId
        || (request.P256dh is not null
            && request.Auth is not null
            && subscription.MatchesCredentials(request.P256dh, request.Auth));
}
