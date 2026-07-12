using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Gamification.Commands;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/achievements")]
public class AchievementsController(IMediator mediator) : ControllerBase
{
    public record ReportEventBody(string EventKey);

    [HttpPost("report-event")]
    [DistributedRateLimit("achievements")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReportEvent(
        [FromBody] ReportEventBody body,
        CancellationToken cancellationToken)
    {
        var command = new ReportEventCommand(HttpContext.GetUserId(), body.EventKey);
        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(value => Ok(value));
    }
}
