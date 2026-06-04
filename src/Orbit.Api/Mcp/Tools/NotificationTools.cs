using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class NotificationTools(IMediator mediator)
{
    [McpServerTool(Name = "get_notifications"), Description("Get the user's notifications (up to 50, newest first) with unread count.")]
    public async Task<string> GetNotifications(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var result = await mediator.Send(new GetNotificationsQuery(userId), cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var items = result.Value.Items;
        if (items.Count == 0)
            return "No notifications.";

        var lines = items.Select(n =>
            $"- [{(n.IsRead ? " " : "NEW")}] {n.Title}: {n.Body} (id: {n.Id}, {n.CreatedAtUtc:yyyy-MM-dd HH:mm})");

        return $"Notifications ({items.Count}, {result.Value.UnreadCount} unread):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "mark_notification_read"), Description("Mark a single notification as read.")]
    public async Task<string> MarkNotificationRead(
        ClaimsPrincipal user,
        [Description("The notification ID (GUID)")] string notificationId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new MarkNotificationReadCommand(userId, McpInputParser.ParseGuid(notificationId, "notificationId"));
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? $"Marked notification {notificationId} as read."
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "mark_all_notifications_read"), Description("Mark all notifications as read.")]
    public async Task<string> MarkAllNotificationsRead(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var result = await mediator.Send(new MarkAllNotificationsReadCommand(userId), cancellationToken);

        return result.IsSuccess
            ? $"Marked {result.Value} notifications as read."
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "delete_notification"), Description("Delete a specific notification.")]
    public async Task<string> DeleteNotification(
        ClaimsPrincipal user,
        [Description("The notification ID (GUID)")] string notificationId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new DeleteNotificationCommand(userId, McpInputParser.ParseGuid(notificationId, "notificationId"));
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? $"Deleted notification {notificationId}."
            : $"Error: {result.Error}";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        if (!Guid.TryParse(claim, out var userId))
            throw new UnauthorizedAccessException("User ID claim is not a valid GUID");
        return userId;
    }
}
