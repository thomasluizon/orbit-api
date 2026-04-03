using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Queries;

#pragma warning disable CA1873

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/notifications")]
public partial class NotificationController(
    IMediator mediator,
    ILogger<NotificationController> logger) : ControllerBase
{
    public record SubscribeRequest(string Endpoint, string P256dh, string Auth);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetNotifications(CancellationToken cancellationToken)
    {
        var query = new GetNotificationsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkAsRead(Guid id, CancellationToken cancellationToken)
    {
        var command = new MarkNotificationReadCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : NotFound(new { error = result.Error });
    }

    [HttpPut("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken cancellationToken)
    {
        var command = new MarkAllNotificationsReadCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteNotificationCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpDelete("all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteAll(CancellationToken cancellationToken)
    {
        var command = new DeleteAllNotificationsCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
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
            request.Auth);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            LogPushSubscriptionRegistered(logger, HttpContext.GetUserId());
            return Ok();
        }

        return BadRequest(new { error = result.Error });
    }

    [HttpPost("unsubscribe")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Unsubscribe(
        [FromBody] SubscribeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UnsubscribePushCommand(HttpContext.GetUserId(), request.Endpoint);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogPushSubscriptionRemoved(logger, HttpContext.GetUserId());

        return result.IsSuccess ? Ok() : BadRequest(new { error = result.Error });
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
            return BadRequest(new { error = result.Error });

        if (result.Value.Status == "failed")
            LogTestPushFailed(logger, HttpContext.GetUserId());

        return Ok(result.Value);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Push subscription registered for user {UserId}")]
    private static partial void LogPushSubscriptionRegistered(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Push subscription removed for user {UserId}")]
    private static partial void LogPushSubscriptionRemoved(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Test push failed for user {UserId}")]
    private static partial void LogTestPushFailed(ILogger logger, Guid userId);
}
