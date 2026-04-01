using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class GamificationController(IMediator mediator) : ControllerBase
{
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var query = new GetGamificationProfileQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, ct);

        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("achievements")]
    public async Task<IActionResult> GetAchievements(CancellationToken ct)
    {
        var query = new GetAchievementsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, ct);

        return result.ToPayGateAwareResult(v => Ok(v));
    }

    [HttpGet("streak")]
    public async Task<IActionResult> GetStreakInfo(CancellationToken ct)
    {
        var query = new GetStreakInfoQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, ct);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    [HttpPost("streak/freeze")]
    public async Task<IActionResult> ActivateStreakFreeze(CancellationToken ct)
    {
        var command = new ActivateStreakFreezeCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, ct);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }
}
