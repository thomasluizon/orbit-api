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
[Route("api/[controller]")]
public class NotificationController(
    IMediator mediator,
    ILogger<NotificationController> logger) : ControllerBase
{
    public record SubscribeRequest(string Endpoint, string P256dh, string Auth);

    [HttpGet]
    public async Task<IActionResult> GetNotifications(CancellationToken ct)
    {
        var query = new GetNotificationsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, ct);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpPut("{id:guid}/read")]
    public async Task<IActionResult> MarkAsRead(Guid id, CancellationToken ct)
    {
        var command = new MarkNotificationReadCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, ct);
        return result.IsSuccess ? Ok() : NotFound();
    }

    [HttpPut("read-all")]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken ct)
    {
        var command = new MarkAllNotificationsReadCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, ct);
        return result.IsSuccess ? Ok() : BadRequest(new { error = result.Error });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var command = new DeleteNotificationCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, ct);
        return result.IsSuccess ? Ok() : BadRequest(new { error = result.Error });
    }

    [HttpDelete("all")]
    public async Task<IActionResult> DeleteAll(CancellationToken ct)
    {
        var command = new DeleteAllNotificationsCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, ct);
        return result.IsSuccess ? Ok() : BadRequest(new { error = result.Error });
    }

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(
        [FromBody] SubscribeRequest request,
        CancellationToken ct)
    {
        var command = new SubscribePushCommand(
            HttpContext.GetUserId(),
            request.Endpoint,
            request.P256dh,
            request.Auth);

        var result = await mediator.Send(command, ct);

        if (result.IsSuccess)
        {
            logger.LogInformation("Push subscription registered for user {UserId}", HttpContext.GetUserId());
            return Ok();
        }

        return BadRequest(new { error = result.Error });
    }

    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe(
        [FromBody] SubscribeRequest request,
        CancellationToken ct)
    {
        var command = new UnsubscribePushCommand(HttpContext.GetUserId(), request.Endpoint);
        var result = await mediator.Send(command, ct);

        if (result.IsSuccess)
            logger.LogInformation("Push subscription removed for user {UserId}", HttpContext.GetUserId());

        return result.IsSuccess ? Ok() : BadRequest(new { error = result.Error });
    }

    [HttpPost("test-push")]
    public async Task<IActionResult> TestPush(CancellationToken ct)
    {
        var command = new TestPushNotificationCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, ct);

        if (result.IsFailure)
            return BadRequest(new { error = result.Error });

        if (result.Value.Status == "failed")
            logger.LogWarning("Test push failed for user {UserId}", HttpContext.GetUserId());

        return Ok(result.Value);
    }
}
