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
        // Check if subscription with this endpoint already exists
        var existing = await pushSubscriptionRepository.FindOneTrackedAsync(
            s => s.Endpoint == request.Endpoint,
            cancellationToken: cancellationToken);

        if (existing is not null)
        {
            // If the same user already owns this endpoint, nothing to do
            if (existing.UserId == request.UserId)
                return Result.Success();

            // Different user owns the endpoint -- remove the old subscription
            pushSubscriptionRepository.Remove(existing);
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
