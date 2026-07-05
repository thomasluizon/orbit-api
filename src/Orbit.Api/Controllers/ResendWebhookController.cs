using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Application.Resend.Commands;

namespace Orbit.Api.Controllers;

[AllowAnonymous]
[ApiController]
[Route("api/webhooks/resend")]
public partial class ResendWebhookController(IMediator mediator, ILogger<ResendWebhookController> logger) : ControllerBase
{
#pragma warning disable S6932 // Raw Request.Body and Request.Headers needed for Svix webhook signature verification
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> HandleWebhook(CancellationToken cancellationToken)
    {
        var payload = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync(cancellationToken);
        var command = new HandleResendWebhookCommand(
            payload,
            Request.Headers["svix-id"].ToString(),
            Request.Headers["svix-timestamp"].ToString(),
            Request.Headers["svix-signature"].ToString());

        var result = await mediator.Send(command, cancellationToken);
        if (result.IsSuccess)
            return Ok();

        LogWebhookRejected(logger, result.Error);
        var failureStatus = result.ErrorCode == ErrorCodes.InvalidResendWebhookSignature
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status500InternalServerError;
        return result.ToErrorResult(failureStatus);
    }
#pragma warning restore S6932

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Resend webhook rejected: {Error}")]
    private static partial void LogWebhookRejected(ILogger logger, string? error);
}
