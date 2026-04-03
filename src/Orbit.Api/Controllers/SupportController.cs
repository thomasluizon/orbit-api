using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Microsoft.AspNetCore.RateLimiting;
using Orbit.Application.Support.Commands;

#pragma warning disable CA1873

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class SupportController(IMediator mediator, ILogger<SupportController> logger) : ControllerBase
{
    public record SupportRequest(string Name, string Email, string Subject, string Message);

    [HttpPost]
    [EnableRateLimiting("support")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SendSupport(
        [FromBody] SupportRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SendSupportCommand(
            HttpContext.GetUserId(),
            request.Name,
            request.Email,
            request.Subject,
            request.Message);

        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogSupportRequestSent(logger, HttpContext.GetUserId(), request.Subject);

        return result.IsSuccess
            ? Ok(new { message = "Support request sent successfully" })
            : BadRequest(new { error = result.Error });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Support request sent by user {UserId} subject {Subject}")]
    private static partial void LogSupportRequestSent(ILogger logger, Guid userId, string subject);

}
