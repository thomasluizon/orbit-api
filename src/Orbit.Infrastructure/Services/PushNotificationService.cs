using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using WebPush;

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

        var client = new WebPushClient();
        var vapidDetails = new VapidDetails(_settings.Subject, _settings.PublicKey, _settings.PrivateKey);

        var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body, url });

        var staleSubscriptions = new List<Domain.Entities.PushSubscription>();

        foreach (var sub in subscriptions)
        {
            try
            {
                var pushSubscription = new WebPush.PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await client.SendNotificationAsync(pushSubscription, payload, vapidDetails, cancellationToken);
            }
            catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                logger.LogInformation("Push subscription {Endpoint} is gone, removing", sub.Endpoint);
                staleSubscriptions.Add(sub);
            }
            catch (WebPushException ex)
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
