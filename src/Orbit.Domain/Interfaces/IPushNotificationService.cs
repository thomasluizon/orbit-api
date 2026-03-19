namespace Orbit.Domain.Interfaces;

public interface IPushNotificationService
{
    Task SendToUserAsync(Guid userId, string title, string body, string? url = null, CancellationToken cancellationToken = default);
}
