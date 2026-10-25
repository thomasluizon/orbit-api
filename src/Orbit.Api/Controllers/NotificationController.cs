using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/notifications")]
public partial class NotificationController(
    IMediator mediator,
    ILogger<NotificationController> logger) : ControllerBase
{
    public record SubscribeRequest(string Endpoint, string P256dh, string Auth, string? ApnsTopic = null, Guid? DeviceId = null);

    public record UnsubscribeRequest(
        string Endpoint,
        string? P256dh = null,
        string? Auth = null,
        bool ReleaseOtherAccount = false);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetNotifications(CancellationToken cancellationToken)
    {
        var query = new GetNotificationsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("subscriptions")]
    [ProducesResponseType(typeof(GetPushSubscriptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSubscriptions(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPushSubscriptionsQuery(HttpContext.GetUserId()), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpPut("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkAsRead(Guid id, CancellationToken cancellationToken)
    {
        var command = new MarkNotificationReadCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken cancellationToken)
    {
        var command = new MarkAllNotificationsReadCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteNotificationCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpDelete("all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteAll(CancellationToken cancellationToken)
    {
        var command = new DeleteAllNotificationsCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPost("subscribe")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Subscribe(
        [FromBody] SubscribeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SubscribePushCommand(
            HttpContext.GetUserId(),
            request.Endpoint,
            request.P256dh,
            request.Auth,
            request.ApnsTopic,
            request.DeviceId);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogPushSubscriptionRegistered(HttpContext.GetUserId());
            return Ok();
        }

        return result.ToErrorResult();
    }

    [HttpPost("unsubscribe")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Unsubscribe(
        [FromBody] UnsubscribeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UnsubscribePushCommand(
            HttpContext.GetUserId(),
            request.Endpoint,
            request.P256dh,
            request.Auth,
            request.ReleaseOtherAccount);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogPushSubscriptionRemoved(HttpContext.GetUserId());

        return result.ToPayGateAwareResult();
    }

    [HttpPost("test-push")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> TestPush(CancellationToken cancellationToken)
    {
        var command = new TestPushNotificationCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsFailure)
            return result.ToErrorResult();

        if (result.Value.Status == "failed" && logger.IsEnabled(LogLevel.Warning))
            logger.LogTestPushFailed(HttpContext.GetUserId());

        return Ok(result.Value);
    }
}

internal static partial class NotificationControllerLogging
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Push subscription registered for user {UserId}")]
    internal static partial void LogPushSubscriptionRegistered(this ILogger logger, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Push subscription removed for user {UserId}")]
    internal static partial void LogPushSubscriptionRemoved(this ILogger logger, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Test push failed for user {UserId}")]
    internal static partial void LogTestPushFailed(this ILogger logger, Guid userId);
}
