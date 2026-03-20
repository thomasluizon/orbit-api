using System.Net;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class NotificationController(
    OrbitDbContext dbContext,
    ILogger<NotificationController> logger) : ControllerBase
{
    public record SubscribeRequest(string Endpoint, string P256dh, string Auth);
    public record NotificationItem(Guid Id, string Title, string Body, string? Url, Guid? HabitId, bool IsRead, DateTime CreatedAtUtc);

    [HttpGet]
    public async Task<IActionResult> GetNotifications(CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var notifications = await dbContext.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(50)
            .Select(n => new NotificationItem(n.Id, n.Title, n.Body, n.Url, n.HabitId, n.IsRead, n.CreatedAtUtc))
            .ToListAsync(ct);

        var unreadCount = await dbContext.Notifications
            .CountAsync(n => n.UserId == userId && !n.IsRead, ct);

        return Ok(new { items = notifications, unreadCount });
    }

    [HttpPut("{id:guid}/read")]
    public async Task<IActionResult> MarkAsRead(Guid id, CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var notification = await dbContext.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);

        if (notification is null) return NotFound();

        notification.MarkAsRead();
        await dbContext.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPut("read-all")]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        await dbContext.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
        return Ok();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var notification = await dbContext.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);

        if (notification is not null)
        {
            dbContext.Notifications.Remove(notification);
            await dbContext.SaveChangesAsync(ct);
        }
        return Ok();
    }

    [HttpDelete("all")]
    public async Task<IActionResult> DeleteAll(CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        await dbContext.Notifications
            .Where(n => n.UserId == userId)
            .ExecuteDeleteAsync(ct);
        return Ok();
    }

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(
        [FromBody] SubscribeRequest request,
        CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();

        var existing = await dbContext.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);

        if (existing is not null)
        {
            if (existing.UserId == userId)
                return Ok();

            dbContext.PushSubscriptions.Remove(existing);
        }

        var result = Domain.Entities.PushSubscription.Create(userId, request.Endpoint, request.P256dh, request.Auth);
        if (result.IsFailure)
            return BadRequest(new { error = result.Error });

        await dbContext.PushSubscriptions.AddAsync(result.Value, ct);
        await dbContext.SaveChangesAsync(ct);

        return Ok();
    }

    [HttpPost("test-push")]
    public async Task<IActionResult> TestPush(
        [FromServices] IOptions<VapidSettings> vapidSettings,
        CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var settings = vapidSettings.Value;

        var subscriptions = await dbContext.PushSubscriptions
            .Where(s => s.UserId == userId)
            .ToListAsync(ct);

        if (subscriptions.Count == 0)
            return BadRequest(new { error = "No push subscriptions found for this user" });

        var client = new PushServiceClient();
        client.DefaultAuthentication = new VapidAuthentication(
            settings.PublicKey, settings.PrivateKey)
        {
            Subject = settings.Subject
        };

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            title = "Orbit Test",
            body = "Push notifications are working!",
            url = "/"
        });
        var message = new PushMessage(payload) { TimeToLive = 60 };

        var results = new List<object>();
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
                await client.RequestPushMessageDeliveryAsync(pushSub, message, ct);
                logger.LogInformation("Test push sent to {Endpoint}", sub.Endpoint);
                results.Add(new { endpoint = sub.Endpoint[..Math.Min(60, sub.Endpoint.Length)] + "...", status = "sent" });
            }
            catch (PushServiceClientException ex) when (ex.StatusCode == HttpStatusCode.Gone || ex.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogWarning("Test push: subscription {Endpoint} is gone, removing", sub.Endpoint);
                dbContext.PushSubscriptions.Remove(sub);
                results.Add(new { endpoint = sub.Endpoint[..Math.Min(60, sub.Endpoint.Length)] + "...", status = "failed", error = $"{ex.StatusCode}: subscription expired - toggle push off/on to fix" });
            }
            catch (PushServiceClientException ex)
            {
                logger.LogWarning("Test push failed for {Endpoint}: {Status} {Message}", sub.Endpoint, ex.StatusCode, ex.Message);
                results.Add(new { endpoint = sub.Endpoint[..Math.Min(60, sub.Endpoint.Length)] + "...", status = "failed", error = $"{ex.StatusCode}: {ex.Message}" });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Test push failed for {Endpoint}", sub.Endpoint);
                results.Add(new { endpoint = sub.Endpoint[..Math.Min(60, sub.Endpoint.Length)] + "...", status = "failed", error = ex.Message });
            }
        }

        await dbContext.SaveChangesAsync(ct);
        return Ok(new { subscriptionCount = subscriptions.Count, results });
    }

    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe(
        [FromBody] SubscribeRequest request,
        CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();

        var subscription = await dbContext.PushSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == request.Endpoint, ct);

        if (subscription is not null)
        {
            dbContext.PushSubscriptions.Remove(subscription);
            await dbContext.SaveChangesAsync(ct);
        }

        return Ok();
    }
}
