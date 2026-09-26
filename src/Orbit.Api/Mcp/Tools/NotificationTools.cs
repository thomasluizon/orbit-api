using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Notifications.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class NotificationTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    private const string UpdateNotificationsTool = "update_notifications";

    [McpServerTool(Name = "get_notifications"), Description("Get the user's notifications (up to 50, newest first) with unread count.")]
    public async Task<string> GetNotifications(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var result = await mediator.Send(new GetNotificationsQuery(userId), cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var items = result.Value.Items;
        if (items.Count == 0)
            return "No notifications.";

        var lines = items.Select(n =>
            // Body omitted: social/accountability bodies embed other users' names; the MCP surface must not leak third-party PII. https://github.com/thomasluizon/orbit-ui-mobile/issues/243
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"- [{(n.IsRead ? " " : "NEW")}] {n.Title} (id: {n.Id}, {n.CreatedAtUtc:yyyy-MM-dd HH:mm})"));

        return $"Notifications ({items.Count}, {result.Value.UnreadCount} unread):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "mark_notification_read"), Description("Mark a single notification as read.")]
    public async Task<string> MarkNotificationRead(
        ClaimsPrincipal user,
        [Description("The notification ID (GUID)")] string notificationId,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, UpdateNotificationsTool, new
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
        var result = await executorBridge.ExecuteAsync(user, UpdateNotificationsTool, new
        {
            action = "mark_all_read"
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? "Marked all notifications as read." : result.Message;
    }

    [McpServerTool(Name = "subscribe_push"), Description("Register a Web Push subscription so the user receives push notifications.")]
    public async Task<string> SubscribePush(
        ClaimsPrincipal user,
        [Description("Push service endpoint URL")] string endpoint,
        [Description("Client public key (p256dh)")] string p256dh,
        [Description("Client auth secret")] string auth,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, UpdateNotificationsTool, new
        {
            action = "subscribe_push",
            endpoint,
            p256dh,
            auth
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? "Push subscription registered." : result.Message;
    }

    [McpServerTool(Name = "unsubscribe_push"), Description("Remove a previously registered Web Push subscription.")]
    public async Task<string> UnsubscribePush(
        ClaimsPrincipal user,
        [Description("Push service endpoint URL to remove")] string endpoint,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, UpdateNotificationsTool, new
        {
            action = "unsubscribe_push",
            endpoint
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? "Push subscription removed." : result.Message;
    }

    [McpServerTool(Name = "test_push"), Description("Send a test push notification to the user's registered devices.")]
    public async Task<string> TestPush(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, UpdateNotificationsTool, new
        {
            action = "test_push"
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? "Test push requested." : result.Message;
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
}
