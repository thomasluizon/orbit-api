using System.Net;
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
    ILogger<PushNotificationService> logger) : IPushNotificationService
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

        var client = new PushServiceClient();
        client.DefaultAuthentication = new VapidAuthentication(
            _settings.PublicKey, _settings.PrivateKey)
        {
            Subject = _settings.Subject
        };

        var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body, url });
        var message = new PushMessage(payload) { TimeToLive = 3600 };

        var staleSubscriptions = new List<Domain.Entities.PushSubscription>();

        foreach (var sub in subscriptions)
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
                await client.RequestPushMessageDeliveryAsync(pushSub, message, cancellationToken);
                logger.LogInformation("Push sent to {Endpoint}", sub.Endpoint);
            }
            catch (PushServiceClientException ex)
                when (ex.StatusCode == HttpStatusCode.Gone || ex.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Push subscription {Endpoint} is gone, removing", sub.Endpoint);
                staleSubscriptions.Add(sub);
            }
            catch (PushServiceClientException ex)
            {
                logger.LogWarning("Failed to send push to {Endpoint}: {Status} {Message}", sub.Endpoint, ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send push notification to {Endpoint}", sub.Endpoint);
            }
        }

        if (staleSubscriptions.Count > 0)
        {
            dbContext.PushSubscriptions.RemoveRange(staleSubscriptions);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
