using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class CalendarController(IMediator mediator, ILogger<CalendarController> logger) : ControllerBase
{
    [HttpGet("events")]
    public async Task<IActionResult> GetEvents(CancellationToken cancellationToken)
    {
        var query = new GetCalendarEventsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsSuccess)
            return Ok(result.Value);

        logger.LogWarning("Failed to fetch calendar events: {Error}", result.Error);
        return BadRequest(new { error = result.Error });
    }

    public record ImportRequest(IReadOnlyList<string> EventIds);

    [HttpPost("import")]
    public async Task<IActionResult> Import(
        [FromBody] ImportRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ImportCalendarEventsCommand(HttpContext.GetUserId(), request.EventIds);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            return Ok(result.Value);

        logger.LogWarning("Failed to import calendar events: {Error}", result.Error);
        return BadRequest(new { error = result.Error });
    }
}
