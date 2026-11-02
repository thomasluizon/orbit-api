using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Notifications.Queries;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class NotificationTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    private const string UpdateNotificationsTool = "update_notifications";
    [McpServerTool(Name = "get_notifications"), Description("Get the user's notifications (up to 50, newest first) with unread count.")]
    public async Task<CallToolResult> GetNotifications(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var result = await mediator.Send(new GetNotificationsQuery(userId), cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var items = result.Value.Items;
            if (items.Count == 0)
                return McpToolHelpers.Text("No notifications.");
            var lines = items.Select(n => // Body omitted: social/accountability bodies embed other users' names; the MCP surface must not leak third-party PII. https://github.com/thomasluizon/orbit-ui-mobile/issues/243
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"- [{(n.IsRead ? " " : "NEW")}] {n.Title} (id: {n.Id}, {n.CreatedAtUtc:yyyy-MM-dd HH:mm})"));
            return McpToolHelpers.Text($"Notifications ({items.Count}, {result.Value.UnreadCount} unread):\n{string.Join("\n", lines)}");
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "mark_notification_read"), Description("Mark a single notification as read.")]
    public async Task<CallToolResult> MarkNotificationRead(
        ClaimsPrincipal user,
        [Description("The notification ID (GUID)")] string notificationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(notificationId, "notificationId");
            var result = await executorBridge.ExecuteAsync(user, UpdateNotificationsTool, new { action = "mark_read", notification_id = notificationId }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Marked notification {notificationId} as read.") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "mark_all_notifications_read"), Description("Mark all notifications as read.")]
    public async Task<CallToolResult> MarkAllNotificationsRead(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, UpdateNotificationsTool, new { action = "mark_all_read" }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text("Marked all notifications as read.") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "subscribe_push"), Description("Register a Web Push subscription so the user receives push notifications.")]
    public async Task<CallToolResult> SubscribePush(
        ClaimsPrincipal user,
        [Description("Push service endpoint URL")] string endpoint,
        [Description("Client public key (p256dh)")] string p256dh,
        [Description("Client auth secret")] string auth,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, UpdateNotificationsTool, new { action = "subscribe_push", endpoint, p256dh, auth }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text("Push subscription registered.") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "unsubscribe_push"), Description("Remove a previously registered Web Push subscription.")]
    public async Task<CallToolResult> UnsubscribePush(
        ClaimsPrincipal user,
        [Description("Push service endpoint URL to remove")] string endpoint,
        [Description("Device public key, or fcm for an Android registration")] string? p256dh = null,
        [Description("Device authentication secret, or fcm for an Android registration")] string? auth = null,
        [Description("Explicitly release another account's registration with device credentials; omit for ordinary sign-out")] bool releaseOtherAccount = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, UpdateNotificationsTool, new { action = "unsubscribe_push", endpoint, p256dh, auth, release_other_account = releaseOtherAccount }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text("Push subscription removed.") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "test_push"), Description("Send a test push notification to the user's registered devices.")]
    public async Task<CallToolResult> TestPush(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, UpdateNotificationsTool, new { action = "test_push" }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text("Test push requested.") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "delete_notification"), Description("Delete a specific notification.")]
    public async Task<CallToolResult> DeleteNotification(
        ClaimsPrincipal user,
        [Description("The notification ID (GUID)")] string notificationId,
        [Description("Confirmation token returned by confirm_agent_operation_v2 (required: deleting a notification is destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(notificationId, "notificationId");
            var result = await executorBridge.ExecuteAsync(user, "delete_notifications", new { action = "delete_one", notification_id = notificationId }, confirmationToken, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Deleted notification {notificationId}.") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }
}
