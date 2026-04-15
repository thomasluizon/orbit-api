using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Commands;

public record SubscribePushCommand(
    Guid UserId,
    string Endpoint,
    string P256dh,
    string Auth) : IRequest<Result>;

public class SubscribePushCommandHandler(
    IGenericRepository<PushSubscription> pushSubscriptionRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SubscribePushCommand, Result>
{
    public async Task<Result> Handle(SubscribePushCommand request, CancellationToken cancellationToken)
    {
        // Validate the endpoint URL before storing -- the value flows out as an HTTP request
        // target in PushNotificationService, so a non-https or non-absolute URL is an SSRF
        // vector against internal hosts.
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            return Result.Failure("Push subscription endpoint must be an absolute https:// URL.");
        }

        // Check if subscription with this endpoint already exists
        var existing = await pushSubscriptionRepository.FindOneTrackedAsync(
            s => s.Endpoint == request.Endpoint,
            cancellationToken: cancellationToken);

        if (existing is not null)
        {
            // If the same user already owns this endpoint, nothing to do
            if (existing.UserId == request.UserId)
                return Result.Success();

            // Different user owns this endpoint. Reject -- a legitimate browser re-registration
            // produces a NEW endpoint URL, so a duplicate cross-user collision is most likely
            // an attempt to hijack another user's notification stream.
            return Result.Failure("Push subscription endpoint is already registered to a different user.");
        }

        var result = PushSubscription.Create(request.UserId, request.Endpoint, request.P256dh, request.Auth);
        if (result.IsFailure)
            return Result.Failure(result.Error);

        await pushSubscriptionRepository.AddAsync(result.Value, cancellationToken);

        // Enforce per-user subscription cap: keep only the most recent subscriptions
        var userSubs = await pushSubscriptionRepository.FindTrackedAsync(
            s => s.UserId == request.UserId,
            cancellationToken);

        var orderedSubs = userSubs.OrderByDescending(s => s.CreatedAtUtc).ToList();

        if (orderedSubs.Count >= AppConstants.MaxPushSubscriptionsPerUser)
        {
            var toRemove = orderedSubs.Skip(AppConstants.MaxPushSubscriptionsPerUser - 1);
            pushSubscriptionRepository.RemoveRange(toRemove);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
