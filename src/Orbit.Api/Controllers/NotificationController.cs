using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Orbit.Api.Extensions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Api.Controllers;

// TODO: This controller injects OrbitDbContext directly, bypassing the CQRS/MediatR pattern.
// All read and write operations should be migrated to dedicated Query/Command handlers in
// Orbit.Application. OrbitDbContext should be removed from this controller entirely.
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
        [FromServices] Domain.Interfaces.IPushNotificationService pushService,
        CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();

        var subscriptionCount = await dbContext.PushSubscriptions
            .CountAsync(s => s.UserId == userId, ct);

        if (subscriptionCount == 0)
            return BadRequest(new { error = "No push subscriptions found for this user" });

        try
        {
            await pushService.SendToUserAsync(userId, "Orbit Test", "Push notifications are working!", "/", ct);
            return Ok(new { subscriptionCount, status = "sent" });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Test push failed for user {UserId}", userId);
            return Ok(new { subscriptionCount, status = "failed", error = "Failed to send push notification" });
        }
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
