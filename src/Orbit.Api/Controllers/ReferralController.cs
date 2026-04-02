using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Referrals.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ReferralController(IMediator mediator) : ControllerBase
{
    [HttpGet("code")]
    public async Task<IActionResult> GetOrCreateCode(CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var result = await mediator.Send(new GetOrCreateReferralCodeCommand(userId), ct);

        if (result.IsSuccess)
            return Ok(new { code = result.Value, link = $"https://app.useorbit.org/r/{result.Value}" });

        return BadRequest(new { error = result.Error });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var result = await mediator.Send(new GetReferralStatsQuery(userId), ct);

        if (result.IsSuccess)
            return Ok(result.Value);

        return BadRequest(new { error = result.Error });
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken ct)
    {
        var userId = HttpContext.GetUserId();
        var result = await mediator.Send(new GetReferralDashboardQuery(userId), ct);

        if (result.IsSuccess)
            return Ok(result.Value);

        return BadRequest(new { error = result.Error });
    }
}
