using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class GamificationController(IMediator mediator) : ControllerBase
{
    [HttpGet("profile")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var query = new GetGamificationProfileQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);

        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("achievements")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAchievements(CancellationToken cancellationToken)
    {
        var query = new GetAchievementsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);

        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("streak")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetStreakInfo(CancellationToken cancellationToken)
    {
        var query = new GetStreakInfoQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);

        return result.ToPayGateAwareResult(v => Ok(v));
    }
}
