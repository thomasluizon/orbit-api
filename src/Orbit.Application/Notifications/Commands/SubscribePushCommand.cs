using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
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
        var transport = PushSubscription.ClassifyTransport(request.P256dh);
        if (transport == PushTransport.Fcm)
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint))
                return Result.Failure(ErrorMessages.FcmTokenRequired);
        }
        else if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            return Result.Failure(ErrorMessages.PushEndpointInvalid);
        }

        var existing = await pushSubscriptionRepository.FindOneTrackedAsync(
            s => s.Endpoint == request.Endpoint,
            cancellationToken: cancellationToken);

        if (existing is not null)
        {
            if (existing.UserId == request.UserId)
                return Result.Success();

            return Result.Failure(ErrorMessages.PushEndpointOwnedByOtherUser);
        }

        var result = PushSubscription.Create(request.UserId, request.Endpoint, request.P256dh, request.Auth);
        if (result.IsFailure)
            return result.PropagateError();

        var subscription = result.Value;
        await pushSubscriptionRepository.AddAsync(subscription, cancellationToken);

        EvictOldestBeyondCap(await GetPersistedUserSubscriptions(request.UserId, cancellationToken), subscription);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            return Result.Success();
        }

        return Result.Success();
    }

    private async Task<IReadOnlyList<PushSubscription>> GetPersistedUserSubscriptions(Guid userId, CancellationToken cancellationToken)
    {
        var userSubs = await pushSubscriptionRepository.FindTrackedAsync(
            s => s.UserId == userId,
            cancellationToken);

        return userSubs.OrderBy(s => s.CreatedAtUtc).ToList();
    }

    private void EvictOldestBeyondCap(IReadOnlyList<PushSubscription> persistedOldestFirst, PushSubscription incoming)
    {
        var evictable = persistedOldestFirst.Where(s => s.Id != incoming.Id).ToList();

        var removeCount = evictable.Count + 1 - AppConstants.MaxPushSubscriptionsPerUser;
        if (removeCount <= 0)
            return;

        pushSubscriptionRepository.RemoveRange(evictable.Take(removeCount));
    }
}
