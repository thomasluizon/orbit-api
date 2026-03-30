using System.ComponentModel;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class NotificationTools(OrbitDbContext dbContext)
{
    [McpServerTool(Name = "get_notifications"), Description("Get the user's notifications (up to 50, newest first) with unread count.")]
    public async Task<string> GetNotifications(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);

        var notifications = await dbContext.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        var unreadCount = await dbContext.Notifications
            .CountAsync(n => n.UserId == userId && !n.IsRead, cancellationToken);

        if (notifications.Count == 0)
            return "No notifications.";

        var lines = notifications.Select(n =>
            $"- [{(n.IsRead ? " " : "NEW")}] {n.Title}: {n.Body} (id: {n.Id}, {n.CreatedAtUtc:yyyy-MM-dd HH:mm})");

        return $"Notifications ({notifications.Count}, {unreadCount} unread):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "mark_notification_read"), Description("Mark a single notification as read.")]
    public async Task<string> MarkNotificationRead(
        ClaimsPrincipal user,
        [Description("The notification ID (GUID)")] string notificationId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var notification = await dbContext.Notifications
            .FirstOrDefaultAsync(n => n.Id == Guid.Parse(notificationId) && n.UserId == userId, cancellationToken);

        if (notification is null)
            return "Error: Notification not found.";

        notification.MarkAsRead();
        await dbContext.SaveChangesAsync(cancellationToken);
        return $"Marked notification {notificationId} as read.";
    }

    [McpServerTool(Name = "mark_all_notifications_read"), Description("Mark all notifications as read.")]
    public async Task<string> MarkAllNotificationsRead(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var count = await dbContext.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), cancellationToken);

        return $"Marked {count} notifications as read.";
    }

    [McpServerTool(Name = "delete_notification"), Description("Delete a specific notification.")]
    public async Task<string> DeleteNotification(
        ClaimsPrincipal user,
        [Description("The notification ID (GUID)")] string notificationId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var notification = await dbContext.Notifications
            .FirstOrDefaultAsync(n => n.Id == Guid.Parse(notificationId) && n.UserId == userId, cancellationToken);

        if (notification is null)
            return "Error: Notification not found.";

        dbContext.Notifications.Remove(notification);
        await dbContext.SaveChangesAsync(cancellationToken);
        return $"Deleted notification {notificationId}.";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        return Guid.Parse(claim);
    }
}
