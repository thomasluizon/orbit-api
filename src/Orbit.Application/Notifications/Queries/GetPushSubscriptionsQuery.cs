using System.Security.Cryptography;
using System.Text;
using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Queries;

public record PushSubscriptionItemDto(Guid Id, string Transport, DateTime CreatedAtUtc, string EndpointHash);

public record GetPushSubscriptionsResponse(IReadOnlyList<PushSubscriptionItemDto> Items, int Max);

public record GetPushSubscriptionsQuery(Guid UserId) : IRequest<Result<GetPushSubscriptionsResponse>>;

public class GetPushSubscriptionsQueryHandler(
    IGenericRepository<PushSubscription> repository) : IRequestHandler<GetPushSubscriptionsQuery, Result<GetPushSubscriptionsResponse>>
{
    public async Task<Result<GetPushSubscriptionsResponse>> Handle(GetPushSubscriptionsQuery request, CancellationToken cancellationToken)
    {
        var subscriptions = await repository.FindAsync(
            subscription => subscription.UserId == request.UserId,
            query => query.OrderByDescending(subscription => subscription.CreatedAtUtc).ThenByDescending(subscription => subscription.Id),
            cancellationToken);

        var items = subscriptions.Select(subscription => new PushSubscriptionItemDto(
            subscription.Id,
            subscription.Transport == PushTransport.Fcm ? "native" : "web",
            subscription.CreatedAtUtc,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subscription.Endpoint))))).ToList();

        return Result.Success(new GetPushSubscriptionsResponse(items, AppConstants.MaxPushSubscriptionsPerUser));
    }
}
