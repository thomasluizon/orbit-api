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
public partial class CalendarController(IMediator mediator, ILogger<CalendarController> logger) : ControllerBase
{
    [HttpGet("events")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetEvents(CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var query = new GetCalendarEventsQuery(userId);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsSuccess)
        {
            // Opportunistic sync: if the user opens the sync screen, also run an auto-sync
            // tick as a side effect so active users get fresh suggestions immediately.
            // Gated by the 4h dedupe window inside the command handler.
            try
            {
                await mediator.Send(new RunCalendarAutoSyncCommand(userId, IsOpportunistic: true), cancellationToken);
            }
            catch (Exception ex)
            {
                if (logger.IsEnabled(LogLevel.Warning))
                    LogOpportunisticSyncFailed(logger, ex, userId);
            }

            return Ok(result.Value);
        }

        if (logger.IsEnabled(LogLevel.Warning))
            LogFailedToFetchCalendarEvents(logger, result.Error);
        return BadRequest(new { error = result.Error });
    }

    [HttpPut("dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DismissImport(CancellationToken cancellationToken)
    {
        var command = new DismissCalendarImportCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpGet("auto-sync/state")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAutoSyncState(CancellationToken cancellationToken)
    {
        var query = new GetCalendarAutoSyncStateQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    public record SetAutoSyncRequest(bool Enabled);

    [HttpPut("auto-sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetAutoSync([FromBody] SetAutoSyncRequest request, CancellationToken cancellationToken)
    {
        var command = new SetCalendarAutoSyncCommand(HttpContext.GetUserId(), request.Enabled);
        var result = await mediator.Send(command, cancellationToken);
        if (result.IsFailure)
            return BadRequest(new { error = result.Error, code = result.ErrorCode });
        return Ok(new { success = true });
    }

    [HttpGet("auto-sync/suggestions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSuggestions(CancellationToken cancellationToken)
    {
        var query = new GetCalendarSyncSuggestionsQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error });
    }

    [HttpPut("auto-sync/suggestions/{id:guid}/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DismissSuggestion([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var command = new DismissCalendarSuggestionCommand(HttpContext.GetUserId(), id);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpPost("auto-sync/run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RunSyncNow(CancellationToken cancellationToken)
    {
        var command = new RunCalendarAutoSyncCommand(HttpContext.GetUserId(), IsOpportunistic: true);
        var result = await mediator.Send(command, cancellationToken);
        if (result.IsFailure)
            return BadRequest(new { error = result.Error, code = result.ErrorCode });
        return Ok(result.Value);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to fetch calendar events: {Error}")]
    private static partial void LogFailedToFetchCalendarEvents(ILogger logger, string? error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Opportunistic calendar auto-sync failed for user {UserId}")]
    private static partial void LogOpportunisticSyncFailed(ILogger logger, Exception ex, Guid userId);
}
