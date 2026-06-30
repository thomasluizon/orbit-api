using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Challenges.Queries;
using Orbit.Domain.Enums;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/challenges")]
public partial class ChallengesController(IMediator mediator, ILogger<ChallengesController> logger) : ControllerBase
{
    public record CreateChallengeBody(
        ChallengeType Type,
        string Title,
        string? Description,
        int? TargetCount,
        DateOnly PeriodStartUtc,
        DateOnly? PeriodEndUtc,
        IReadOnlyList<Guid>? LinkedHabitIds,
        IReadOnlyList<Guid>? InvitedFriendUserIds);

    public record JoinChallengeBody(string Code, IReadOnlyList<Guid>? LinkedHabitIds);

    [HttpPost]
    [DistributedRateLimit("challenges")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateChallengeBody body,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var command = new CreateChallengeCommand(
            userId,
            body.Type,
            body.Title,
            body.Description,
            body.TargetCount,
            body.PeriodStartUtc,
            body.PeriodEndUtc,
            body.LinkedHabitIds ?? [],
            body.InvitedFriendUserIds ?? []);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogChallengeCreated(logger, userId);

        return result.ToPayGateAwareResult(id => Ok(new { id }));
    }

    [HttpPost("join")]
    [DistributedRateLimit("challenges")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Join(
        [FromBody] JoinChallengeBody body,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var command = new JoinChallengeCommand(userId, body.Code, body.LinkedHabitIds ?? []);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogChallengeJoined(logger, userId);

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpDelete("{challengeId:guid}/leave")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Leave(Guid challengeId, CancellationToken cancellationToken)
    {
        var command = new LeaveChallengeCommand(HttpContext.GetUserId(), challengeId);
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpGet("{challengeId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(Guid challengeId, CancellationToken cancellationToken)
    {
        var query = new GetChallengeDetailQuery(HttpContext.GetUserId(), challengeId);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Challenge created by user {UserId}")]
    private static partial void LogChallengeCreated(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Challenge joined by user {UserId}")]
    private static partial void LogChallengeJoined(ILogger logger, Guid userId);
}
