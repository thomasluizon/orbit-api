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
    string Auth,
    string? ApnsTopic = null,
    Guid? DeviceId = null) : IRequest<Result>;

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
        else if (transport == PushTransport.WebPush && !WebPushEndpointPolicy.IsAllowed(request.Endpoint))
        {
            return Result.Failure(ErrorMessages.PushEndpointInvalid);
        }

        var result = PushSubscription.Create(request.UserId, request.Endpoint, request.P256dh, request.Auth, request.ApnsTopic, request.DeviceId);
        if (result.IsFailure)
            return result.PropagateError();

        var existing = await pushSubscriptionRepository.FindOneTrackedAsync(
            s => s.Endpoint == request.Endpoint,
            cancellationToken: cancellationToken);

        if (existing is not null)
            return await ClaimExisting(existing, request, cancellationToken);

        if (request.DeviceId is not null)
        {
            var device = await pushSubscriptionRepository.FindOneTrackedAsync(
                s => s.UserId == request.UserId && s.DeviceId == request.DeviceId && s.P256dh == request.P256dh,
                cancellationToken: cancellationToken);
            if (device is not null)
            {
                var renewal = device.RenewRegistration(request.Endpoint, request.Auth, request.ApnsTopic, request.DeviceId);
                if (renewal.IsFailure)
                    return renewal;
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }
        }

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

    /// <summary>
    /// A device keeps one endpoint across the accounts that sign in on it. When another account
    /// registers it and proves it holds the device's credentials, the row moves to that account, so
    /// the previous account neither lists nor counts it toward the device cap.
    /// </summary>
    private async Task<Result> ClaimExisting(
        PushSubscription existing,
        SubscribePushCommand request,
        CancellationToken cancellationToken)
    {
        if (existing.UserId == request.UserId && request.DeviceId is null && request.ApnsTopic is null)
            return Result.Success();

        if (!existing.MatchesCredentials(request.P256dh, request.Auth))
            return Result.Failure(ErrorMessages.PushEndpointOwnedByOtherUser);

        var transfer = existing.TransferTo(request.UserId);
        if (transfer.IsFailure)
            return transfer;

        IReadOnlyList<PushSubscription> previous = [];
        if (request.DeviceId is not null || request.ApnsTopic is not null)
        {
            var renewal = existing.RenewRegistration(request.Endpoint, request.Auth, request.ApnsTopic, request.DeviceId);
            if (renewal.IsFailure)
                return renewal;
            if (request.DeviceId is not null)
            {
                previous = (await pushSubscriptionRepository.FindTrackedAsync(
                    s => s.UserId == request.UserId && s.DeviceId == request.DeviceId
                        && s.P256dh == request.P256dh && s.Id != existing.Id,
                    cancellationToken)).ToList();
                pushSubscriptionRepository.RemoveRange(previous);
            }
        }

        var subscriptions = await GetPersistedUserSubscriptions(request.UserId, cancellationToken);
        var removedIds = previous.Select(subscription => subscription.Id).ToHashSet();
        EvictOldestBeyondCap(subscriptions.Where(subscription => !removedIds.Contains(subscription.Id)).ToList(), existing);

        await unitOfWork.SaveChangesAsync(cancellationToken);
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
