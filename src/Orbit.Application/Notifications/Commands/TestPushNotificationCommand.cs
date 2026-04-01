using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Notifications.Commands;

public record TestPushNotificationResponse(int SubscriptionCount, string Status, string? Error = null);

public record TestPushNotificationCommand(Guid UserId) : IRequest<Result<TestPushNotificationResponse>>;

public class TestPushNotificationCommandHandler(
    IGenericRepository<PushSubscription> pushSubscriptionRepository,
    IPushNotificationService pushService) : IRequestHandler<TestPushNotificationCommand, Result<TestPushNotificationResponse>>
{
    public async Task<Result<TestPushNotificationResponse>> Handle(TestPushNotificationCommand request, CancellationToken cancellationToken)
    {
        var subscriptionCount = await pushSubscriptionRepository.CountAsync(
            s => s.UserId == request.UserId,
            cancellationToken);

        if (subscriptionCount == 0)
            return Result.Failure<TestPushNotificationResponse>(ErrorMessages.NoPushSubscriptions);

        try
        {
            await pushService.SendToUserAsync(request.UserId, "Orbit Test", "Push notifications are working!", "/", cancellationToken);
            return Result.Success(new TestPushNotificationResponse(subscriptionCount, "sent"));
        }
        catch
        {
            return Result.Success(new TestPushNotificationResponse(subscriptionCount, "failed", "Failed to send push notification"));
        }
    }
}
