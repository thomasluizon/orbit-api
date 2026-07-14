using System.Text.Json;
using MediatR;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Queries;
using Orbit.Domain.Common;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetNotificationsTool(IMediator mediator) : IAiTool
{
    public string Name => "get_notifications";
    public string Description => "Read the user's latest notifications and unread count.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new { }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new GetNotificationsQuery(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, Payload: result.Value)
            : ToolResult.FromFailure(result);
    }
}

public class UpdateNotificationsTool(IMediator mediator) : IAiTool
{
    public string Name => "update_notifications";
    public string Description => "Mark notifications as read, manage push subscriptions, or send a test push notification.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new
            {
                type = JsonSchemaTypes.String,
                @enum = new[] { "mark_read", "mark_all_read", "subscribe_push", "unsubscribe_push", "test_push" }
            },
            notification_id = new { type = JsonSchemaTypes.String, nullable = true },
            endpoint = new { type = JsonSchemaTypes.String, nullable = true },
            p256dh = new { type = JsonSchemaTypes.String, nullable = true },
            auth = new { type = JsonSchemaTypes.String, nullable = true }
        },
        required = new[] { "action" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "mark_read" => await MarkReadAsync(args, userId, ct),
            "mark_all_read" => await MarkAllReadAsync(userId, ct),
            "subscribe_push" => await SubscribeAsync(args, userId, ct),
            "unsubscribe_push" => await UnsubscribeAsync(args, userId, ct),
            "test_push" => await TestPushAsync(userId, ct),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> MarkReadAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var notificationId = JsonArgumentParser.GetOptionalString(args, "notification_id");
        if (!Guid.TryParse(notificationId, out var parsedId))
            return new ToolResult(false, Error: "notification_id must be a valid GUID.");

        return await ChatToolMediator.RunAsync(
            mediator,
            new MarkNotificationReadCommand(userId, parsedId),
            parsedId,
            "Marked notification as read",
            new { action = "mark_read", notificationId },
            ct);
    }

    private async Task<ToolResult> SubscribeAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var endpoint = JsonArgumentParser.GetOptionalString(args, "endpoint");
        var p256dh = JsonArgumentParser.GetOptionalString(args, "p256dh");
        var auth = JsonArgumentParser.GetOptionalString(args, "auth");

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(p256dh) || string.IsNullOrWhiteSpace(auth))
            return new ToolResult(false, Error: "endpoint, p256dh, and auth are required.");

        return await ChatToolMediator.RunAsync(
            mediator,
            new SubscribePushCommand(userId, endpoint, p256dh, auth),
            userId,
            "Push subscription registered",
            new { action = "subscribe_push", endpoint },
            ct);
    }

    private async Task<ToolResult> UnsubscribeAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var endpoint = JsonArgumentParser.GetOptionalString(args, "endpoint");
        if (string.IsNullOrWhiteSpace(endpoint))
            return new ToolResult(false, Error: "endpoint is required.");

        return await ChatToolMediator.RunAsync(
            mediator,
            new UnsubscribePushCommand(userId, endpoint),
            userId,
            "Push subscription removed",
            new { action = "unsubscribe_push", endpoint },
            ct);
    }

    private async Task<ToolResult> MarkAllReadAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new MarkAllNotificationsReadCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Marked all notifications as read", Payload: new { action = "mark_all_read", markedCount = result.Value })
            : ToolResult.FromFailure(result, userId.ToString());
    }

    private async Task<ToolResult> TestPushAsync(Guid userId, CancellationToken ct)
    {
        var result = await mediator.Send(new TestPushNotificationCommand(userId), ct);
        return result.IsSuccess
            ? new ToolResult(true, EntityId: userId.ToString(), EntityName: "Test push requested", Payload: result.Value)
            : ToolResult.FromFailure(result, userId.ToString());
    }
}

public class DeleteNotificationsTool(IMediator mediator) : IAiTool
{
    public string Name => "delete_notifications";
    public string Description => "Delete one notification or clear all notifications.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            action = new { type = JsonSchemaTypes.String, @enum = new[] { "delete_one", "delete_all" } },
            notification_id = new { type = JsonSchemaTypes.String, nullable = true }
        },
        required = new[] { "action" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var action = JsonArgumentParser.GetOptionalString(args, "action");
        if (string.IsNullOrWhiteSpace(action))
            return new ToolResult(false, Error: "action is required.");

        return action switch
        {
            "delete_one" => await DeleteOneAsync(args, userId, ct),
            "delete_all" => await ChatToolMediator.RunAsync(mediator, new DeleteAllNotificationsCommand(userId), userId, "Deleted all notifications", new { action }, ct),
            _ => new ToolResult(false, Error: $"Unsupported action '{action}'.")
        };
    }

    private async Task<ToolResult> DeleteOneAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var notificationId = JsonArgumentParser.GetOptionalString(args, "notification_id");
        if (!Guid.TryParse(notificationId, out var parsedId))
            return new ToolResult(false, Error: "notification_id must be a valid GUID.");

        return await ChatToolMediator.RunAsync(
            mediator,
            new DeleteNotificationCommand(userId, parsedId),
            parsedId,
            "Deleted notification",
            new { action = "delete_one", notificationId },
            ct);
    }
}
