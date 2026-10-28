namespace Orbit.Domain.Interfaces;

public interface IPushNotificationService
{
    Task SendToUserAsync(Guid userId, string title, string body, string? url = null, CancellationToken cancellationToken = default);

    Task SendReminderToUserAsync(Guid userId, string title, string body, IReadOnlyDictionary<string, string> data,
        string? url = null, CancellationToken cancellationToken = default);

    Task<bool> TrySendToUserAsync(Guid userId, string title, string body, string? url = null, CancellationToken cancellationToken = default);
}
