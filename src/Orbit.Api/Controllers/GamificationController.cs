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
    public async Task<IActionResult> GetProfile(CancellationToken ct)
    {
        var query = new GetGamificationProfileQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, ct);

        if (result.IsSuccess) return Ok(result.Value);
        if (result.ErrorCode == "PAY_GATE") return StatusCode(403, new { error = result.Error, code = "PAY_GATE" });
        return BadRequest(new { error = result.Error });
    }

    [HttpGet("achievements")]
    public async Task<IActionResult> GetAchievements(CancellationToken ct)
    {
        var query = new GetAchievementsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, ct);

        if (result.IsSuccess) return Ok(result.Value);
        if (result.ErrorCode == "PAY_GATE") return StatusCode(403, new { error = result.Error, code = "PAY_GATE" });
        return BadRequest(new { error = result.Error });
    }
}
