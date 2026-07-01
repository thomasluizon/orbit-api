using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Services;

/// <summary>
/// Delivers a social event to both channels: <see cref="StageAsync"/> adds the in-app notification
/// to the caller's unit of work so it commits atomically with the triggering mutation, and
/// <see cref="PushAsync"/> sends the matching push after the caller commits. Push failures are
/// logged and swallowed: the persisted notification is the source of truth.
/// </summary>
public partial class SocialNotificationDispatcher(
    IGenericRepository<Notification> notificationRepository,
    IPushNotificationService pushNotificationService,
    ILogger<SocialNotificationDispatcher> logger)
{
    public Task StageAsync(Notification notification, CancellationToken cancellationToken) =>
        notificationRepository.AddAsync(notification, cancellationToken);

    public async Task PushAsync(Notification notification, CancellationToken cancellationToken)
    {
        try
        {
            await pushNotificationService.SendToUserAsync(
                notification.UserId, notification.Title, notification.Body, notification.Url, cancellationToken);
        }
        catch (Exception ex)
        {
            LogPushNotificationFailed(logger, ex, notification.UserId, notification.Url);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Social notification push failed for user {UserId} (url {Url})")]
    private static partial void LogPushNotificationFailed(ILogger logger, Exception ex, Guid userId, string? url);
}
