using System.Net;
using FirebaseAdmin.Messaging;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public partial class PushNotificationService(
    OrbitDbContext dbContext,
    IOptions<VapidSettings> vapidSettings,
    ILogger<PushNotificationService> logger,
    HttpClient httpClient) : IPushNotificationService
{
    private readonly VapidSettings _settings = vapidSettings.Value;

    public async Task SendToUserAsync(
        Guid userId,
        string title,
        string body,
        string? url = null,
        CancellationToken cancellationToken = default)
    {
        var subscriptions = await dbContext.PushSubscriptions
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken);

        if (subscriptions.Count == 0) return;

        var fcmSubs = subscriptions.Where(s => s.P256dh == "fcm").ToList();
        var webPushSubs = subscriptions.Where(s => s.P256dh != "fcm").ToList();

        var staleSubscriptions = new List<Domain.Entities.PushSubscription>();

        if (fcmSubs.Count > 0)
            await SendFcm(fcmSubs, title, body, url, staleSubscriptions, cancellationToken);

        if (webPushSubs.Count > 0)
            await SendWebPush(webPushSubs, title, body, url, staleSubscriptions, cancellationToken);

        if (staleSubscriptions.Count > 0)
        {
            dbContext.PushSubscriptions.RemoveRange(staleSubscriptions);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SendFcm(
        List<Domain.Entities.PushSubscription> subs,
        string title, string body, string? url,
        List<Domain.Entities.PushSubscription> staleSubscriptions,
        CancellationToken ct)
    {
        if (FirebaseMessaging.DefaultInstance is null)
        {
            if (logger.IsEnabled(LogLevel.Warning))
                LogFcmNotInitialized(logger, subs.Count);
            return;
        }

        foreach (var sub in subs)
        {
            try
            {
                var message = new Message
                {
                    Token = sub.Endpoint,
                    Notification = new Notification { Title = title, Body = body },
                    Data = new Dictionary<string, string> { ["url"] = url ?? "/" }
                };
                await FirebaseMessaging.DefaultInstance.SendAsync(message, ct);
                if (logger.IsEnabled(LogLevel.Information))
                    LogFcmPushSent(logger, sub.Endpoint[..Math.Min(20, sub.Endpoint.Length)] + "...");
            }
            catch (FirebaseMessagingException ex) when (
                ex.MessagingErrorCode == MessagingErrorCode.Unregistered ||
                ex.MessagingErrorCode == MessagingErrorCode.InvalidArgument ||
                ex.MessagingErrorCode == MessagingErrorCode.SenderIdMismatch)
            {
                if (logger.IsEnabled(LogLevel.Information))
                    LogFcmTokenStale(logger, sub.Endpoint[..Math.Min(20, sub.Endpoint.Length)] + "...");
                staleSubscriptions.Add(sub);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    LogFcmPushFailed(logger, ex, sub.Endpoint[..Math.Min(20, sub.Endpoint.Length)] + "...");
            }
        }
    }

    private async Task SendWebPush(
        List<Domain.Entities.PushSubscription> subs,
        string title, string body, string? url,
        List<Domain.Entities.PushSubscription> staleSubscriptions,
        CancellationToken ct)
    {
        // Reuse the injected HttpClient instead of creating a new PushServiceClient per call
        var client = new PushServiceClient(httpClient);
        client.DefaultAuthentication = new VapidAuthentication(
            _settings.PublicKey, _settings.PrivateKey)
        {
            Subject = _settings.Subject
        };

        var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body, url });
        var message = new PushMessage(payload) { TimeToLive = 3600 };

        foreach (var sub in subs)
        {
            try
            {
                var pushSub = new Lib.Net.Http.WebPush.PushSubscription
                {
                    Endpoint = sub.Endpoint,
                    Keys = new Dictionary<string, string>
                    {
                        ["p256dh"] = sub.P256dh,
                        ["auth"] = sub.Auth
                    }
                };
                await client.RequestPushMessageDeliveryAsync(pushSub, message, ct);
                if (logger.IsEnabled(LogLevel.Information))
                    LogWebPushSent(logger, sub.Endpoint);
            }
            catch (PushServiceClientException ex)
                when (ex.StatusCode == HttpStatusCode.Gone || ex.StatusCode == HttpStatusCode.NotFound)
            {
                if (logger.IsEnabled(LogLevel.Information))
                    LogWebPushSubscriptionGone(logger, sub.Endpoint);
                staleSubscriptions.Add(sub);
            }
            catch (PushServiceClientException ex)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    LogWebPushFailed(logger, sub.Endpoint, ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    LogWebPushFailedGeneric(logger, ex, sub.Endpoint);
            }
        }
    }
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "FCM is not initialized (Firebase credentials not configured). Skipping FCM push to {Count} subscription(s).")]
    private static partial void LogFcmNotInitialized(ILogger logger, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "FCM push sent to {Token}")]
    private static partial void LogFcmPushSent(ILogger logger, string token);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "FCM token {Token} is stale, removing")]
    private static partial void LogFcmTokenStale(ILogger logger, string token);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to send FCM push to {Token}")]
    private static partial void LogFcmPushFailed(ILogger logger, Exception ex, string token);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Web push sent to {Endpoint}")]
    private static partial void LogWebPushSent(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Web push subscription {Endpoint} is gone, removing")]
    private static partial void LogWebPushSubscriptionGone(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Failed to send web push to {Endpoint}: {Status} {Message}")]
    private static partial void LogWebPushFailed(ILogger logger, string endpoint, System.Net.HttpStatusCode? status, string message);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Failed to send web push to {Endpoint}")]
    private static partial void LogWebPushFailedGeneric(ILogger logger, Exception ex, string endpoint);



}