using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;
using Orbit.Application.Waitlist.Commands;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public partial class WaitlistController(
    IMediator mediator,
    IOptions<WaitlistSettings> waitlistOptions,
    ILogger<WaitlistController> logger) : ControllerBase
{
    private readonly WaitlistSettings _settings = waitlistOptions.Value;

    public record JoinWaitlistRequest(string Email, string Language = "en");

    [HttpPost]
    [DistributedRateLimit("waitlist")]
    [EnableCors("Landing")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Join(
        [FromBody] JoinWaitlistRequest request,
        CancellationToken cancellationToken)
    {
        await mediator.Send(new JoinWaitlistCommand(request.Email, request.Language), cancellationToken);

        LogWaitlistJoinRequested(logger, HttpContext.GetRequestId());
        return Ok(new { message = "Check your inbox to confirm your spot." });
    }

    [HttpGet("confirm")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Confirm(
        [FromQuery] string token,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ConfirmWaitlistCommand(token), cancellationToken);
        var status = result.IsSuccess ? "ok" : "invalid";

        return Redirect($"{_settings.LandingBaseUrl.TrimEnd('/')}/waitlist-confirmed?status={status}");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Waitlist join requested. RequestId={RequestId}")]
    private static partial void LogWaitlistJoinRequested(ILogger logger, string requestId);
}
