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

public class PushNotificationService(
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
                logger.LogInformation("FCM push sent to {Token}", sub.Endpoint[..Math.Min(20, sub.Endpoint.Length)] + "...");
            }
            catch (FirebaseMessagingException ex) when (
                ex.MessagingErrorCode == MessagingErrorCode.Unregistered ||
                ex.MessagingErrorCode == MessagingErrorCode.InvalidArgument)
            {
                logger.LogInformation("FCM token {Token} is stale, removing", sub.Endpoint[..Math.Min(20, sub.Endpoint.Length)] + "...");
                staleSubscriptions.Add(sub);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send FCM push to {Token}", sub.Endpoint[..Math.Min(20, sub.Endpoint.Length)] + "...");
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
                logger.LogInformation("Web push sent to {Endpoint}", sub.Endpoint);
            }
            catch (PushServiceClientException ex)
                when (ex.StatusCode == HttpStatusCode.Gone || ex.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Web push subscription {Endpoint} is gone, removing", sub.Endpoint);
                staleSubscriptions.Add(sub);
            }
            catch (PushServiceClientException ex)
            {
                logger.LogWarning("Failed to send web push to {Endpoint}: {Status} {Message}", sub.Endpoint, ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send web push to {Endpoint}", sub.Endpoint);
            }
        }
    }
}
