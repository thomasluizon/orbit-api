using Orbit.Application.Notifications;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public sealed class ProactivePushJob(IPushNotificationService push)
{
    public Task ExecuteAsync(Guid userId, string text, CancellationToken cancellationToken) =>
        push.SendToUserAsync(userId, "Orbit", text, NotificationUrls.Chat, cancellationToken);
}
