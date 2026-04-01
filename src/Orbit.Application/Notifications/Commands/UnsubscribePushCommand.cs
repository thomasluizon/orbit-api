using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Commands;

public record UnsubscribePushCommand(
    Guid UserId,
    string Endpoint) : IRequest<Result>;

public class UnsubscribePushCommandHandler(
    IGenericRepository<PushSubscription> pushSubscriptionRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<UnsubscribePushCommand, Result>
{
    public async Task<Result> Handle(UnsubscribePushCommand request, CancellationToken cancellationToken)
    {
        var subscription = await pushSubscriptionRepository.FindOneTrackedAsync(
            s => s.UserId == request.UserId && s.Endpoint == request.Endpoint,
            cancellationToken: cancellationToken);

        if (subscription is not null)
        {
            pushSubscriptionRepository.Remove(subscription);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}
