using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Application.Referrals.Commands;
using Orbit.Application.Referrals.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/referrals")]
public class ReferralController(
    IMediator mediator,
    IOptions<FrontendSettings> frontendSettings) : ControllerBase
{
    [HttpPost("code")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetOrCreateCode(CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var result = await mediator.Send(new GetOrCreateReferralCodeCommand(userId), cancellationToken);

        if (result.IsSuccess)
            return Ok(new { code = result.Value, link = $"{frontendSettings.Value.BaseUrl}/r/{result.Value}" });

        return BadRequest(new { error = result.Error });
    }

    [HttpGet("stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var result = await mediator.Send(new GetReferralStatsQuery(userId), cancellationToken);

        if (result.IsSuccess)
            return Ok(result.Value);

        return BadRequest(new { error = result.Error });
    }

    [HttpGet("dashboard")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var result = await mediator.Send(new GetReferralDashboardQuery(userId), cancellationToken);

        if (result.IsSuccess)
            return Ok(result.Value);

        return BadRequest(new { error = result.Error });
    }
}
