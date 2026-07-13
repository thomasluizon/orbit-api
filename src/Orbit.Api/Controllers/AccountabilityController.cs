using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Accountability.Commands;
using Orbit.Application.Accountability.Queries;
using Orbit.Domain.Enums;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/accountability")]
public partial class AccountabilityController(IMediator mediator, ILogger<AccountabilityController> logger) : ControllerBase
{
    public record InviteAccountabilityBuddyBody([property: JsonRequired] Guid BuddyUserId, [property: JsonRequired] AccountabilityCadence Cadence, IReadOnlyList<Guid> HabitIds);
    public record AcceptAccountabilityPairBody(IReadOnlyList<Guid> HabitIds);
    public record SetAccountabilityHabitsBody(IReadOnlyList<Guid> HabitIds);
    public record CheckInAccountabilityBody(string? Note);

    [HttpGet("pairs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPairs(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAccountabilityPairsQuery(HttpContext.GetUserId()), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpPost("pairs")]
    [DistributedRateLimit("accountability-invites")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Invite(
        [FromBody] InviteAccountabilityBuddyBody body,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var command = new InviteAccountabilityBuddyCommand(userId, body.BuddyUserId, body.Cadence, body.HabitIds);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogInviteSent(logger, userId);

        return result.ToPayGateAwareResult(id => Ok(new { id }));
    }

    [HttpPost("pairs/{pairId:guid}/accept")]
    [DistributedRateLimit("accountability-mutations")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Accept(
        Guid pairId,
        [FromBody] AcceptAccountabilityPairBody body,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var command = new AcceptAccountabilityPairCommand(userId, pairId, body.HabitIds);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogInviteAccepted(logger, userId);

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpDelete("pairs/{pairId:guid}")]
    [DistributedRateLimit("accountability-mutations")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> End(Guid pairId, CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var result = await mediator.Send(new EndAccountabilityPairCommand(userId, pairId), cancellationToken);

        if (result.IsSuccess)
            LogPairEnded(logger, userId);

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("pairs/{pairId:guid}/habits")]
    [DistributedRateLimit("accountability-mutations")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetHabits(
        Guid pairId,
        [FromBody] SetAccountabilityHabitsBody body,
        CancellationToken cancellationToken)
    {
        var command = new SetAccountabilityHabitsCommand(HttpContext.GetUserId(), pairId, body.HabitIds);
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpGet("pairs/{pairId:guid}/check-ins")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCheckIns(Guid pairId, CancellationToken cancellationToken)
    {
        var query = new GetAccountabilityCheckInsQuery(HttpContext.GetUserId(), pairId);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpPost("pairs/{pairId:guid}/check-ins")]
    [DistributedRateLimit("accountability-checkins")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CheckIn(
        Guid pairId,
        [FromBody] CheckInAccountabilityBody body,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var command = new CheckInAccountabilityCommand(userId, pairId, body.Note);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogCheckedIn(logger, userId);

        return result.ToPayGateAwareResult(id => Ok(new { id }));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Accountability invite sent by user {UserId}")]
    private static partial void LogInviteSent(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Accountability invite accepted by user {UserId}")]
    private static partial void LogInviteAccepted(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Accountability pair ended by user {UserId}")]
    private static partial void LogPairEnded(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Accountability check-in by user {UserId}")]
    private static partial void LogCheckedIn(ILogger logger, Guid userId);
}
