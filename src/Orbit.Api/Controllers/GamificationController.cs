using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class GamificationController(IMediator mediator, IUserDateService userDateService) : ControllerBase
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

    [HttpGet("recap")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRecap(
        [FromQuery] string period,
        CancellationToken cancellationToken)
    {
        if (!RetrospectivePeriodRange.IsKnownPeriod(period))
            return BadRequest(ErrorMessages.InvalidPeriod.ToErrorBody());

        var userId = HttpContext.GetUserId();
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(userId, cancellationToken);
        var (dateFrom, dateTo) = RetrospectivePeriodRange.Resolve(period, today, weekStartDay);

        var query = new GetRecapQuery(userId, dateFrom, dateTo, period);
        var result = await mediator.Send(query, cancellationToken);

        return result.ToPayGateAwareResult(v => Ok(v));
    }
}
