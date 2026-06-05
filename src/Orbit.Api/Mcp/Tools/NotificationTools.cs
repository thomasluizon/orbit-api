using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Notifications.Queries;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// MCP notification tools. Mutations route through <see cref="McpExecutorBridge"/> →
/// <see cref="Orbit.Domain.Interfaces.IAgentOperationExecutor"/> with
/// <see cref="Orbit.Domain.Models.AgentExecutionSurface.Mcp"/> for shared policy evaluation and the
/// <c>AgentAuditLogs</c> trail. <c>mark_notification_read</c>/<c>mark_all_notifications_read</c> map
/// to the consolidated <c>update_notifications</c> chat tool and <c>delete_notification</c> maps to
/// <c>delete_notifications</c>, each via an <c>action</c> discriminator; the destructive delete
/// accepts and forwards a confirmation token. The <c>get_notifications</c> read stays on MediatR.
/// </summary>
[McpServerToolType]
public class NotificationTools(IMediator mediator, McpExecutorBridge executorBridge)
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
        var result = await executorBridge.ExecuteAsync(user, "update_notifications", new
        {
            action = "mark_read",
            notification_id = notificationId
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Marked notification {notificationId} as read." : result.Message;
    }

    [McpServerTool(Name = "mark_all_notifications_read"), Description("Mark all notifications as read.")]
    public async Task<string> MarkAllNotificationsRead(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "update_notifications", new
        {
            action = "mark_all_read"
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? "Marked all notifications as read." : result.Message;
    }

    [McpServerTool(Name = "delete_notification"), Description("Delete a specific notification.")]
    public async Task<string> DeleteNotification(
        ClaimsPrincipal user,
        [Description("The notification ID (GUID)")] string notificationId,
        [Description("Confirmation token returned by confirm_agent_operation_v2 (required: deleting a notification is destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "delete_notifications", new
        {
            action = "delete_one",
            notification_id = notificationId
        }, confirmationToken, cancellationToken);

        return result.Succeeded ? $"Deleted notification {notificationId}." : result.Message;
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
