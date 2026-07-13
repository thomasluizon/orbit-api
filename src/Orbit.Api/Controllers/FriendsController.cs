using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Social.Commands;
using Orbit.Application.Social.Queries;
using Orbit.Domain.Enums;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/friends")]
public partial class FriendsController(IMediator mediator, ILogger<FriendsController> logger) : ControllerBase
{
    public record SendFriendRequestBody(string? Handle, string? ReferralCode);
    public record SendCheerBody([property: JsonRequired] Guid RecipientId, Guid? HabitId, string? Note);
    public record BlockUserBody([property: JsonRequired] Guid BlockedUserId);
    public record ReportUserBody([property: JsonRequired] Guid ReportedUserId, [property: JsonRequired] ReportReason Reason, string? Details, Guid? CheerId);

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetFriends(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetFriendsQuery(HttpContext.GetUserId()), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpGet("{userId:guid}/profile")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFriendProfile(Guid userId, CancellationToken cancellationToken)
    {
        var query = new GetFriendProfileQuery(HttpContext.GetUserId(), userId);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpGet("invite-preview")]
    [DistributedRateLimit("invite-preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvitePreview(
        [FromQuery] string? code,
        CancellationToken cancellationToken)
    {
        var query = new GetInvitePreviewQuery(HttpContext.GetUserId(), code ?? string.Empty);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpGet("feed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetFeed(
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = new GetFriendFeedQuery(HttpContext.GetUserId(), cursor, pageSize);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpGet("cheers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCheers(
        [FromQuery] string direction = GetCheersQueryHandler.ReceivedDirection,
        CancellationToken cancellationToken = default)
    {
        var query = new GetCheersQuery(HttpContext.GetUserId(), direction);
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpPost("requests")]
    [DistributedRateLimit("friend-requests")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SendRequest(
        [FromBody] SendFriendRequestBody body,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var command = new SendFriendRequestCommand(userId, body.Handle, body.ReferralCode);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogFriendRequestSent(logger, userId);

        return result.ToPayGateAwareResult(id => Ok(new { id }));
    }

    [HttpPost("requests/{friendshipId:guid}/accept")]
    [DistributedRateLimit("friend-mutations")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcceptRequest(Guid friendshipId, CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var result = await mediator.Send(new AcceptFriendRequestCommand(userId, friendshipId), cancellationToken);

        if (result.IsSuccess)
            LogFriendRequestAccepted(logger, userId);

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpDelete("{friendUserId:guid}")]
    [DistributedRateLimit("friend-mutations")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveFriend(Guid friendUserId, CancellationToken cancellationToken)
    {
        var command = new RemoveFriendCommand(HttpContext.GetUserId(), friendUserId);
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPost("cheers")]
    [DistributedRateLimit("cheers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SendCheer(
        [FromBody] SendCheerBody body,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var command = new SendCheerCommand(userId, body.RecipientId, body.HabitId, body.Note);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogCheerSent(logger, userId);

        return result.ToPayGateAwareResult(id => Ok(new { id }));
    }

    [HttpPost("block")]
    [DistributedRateLimit("block")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Block(
        [FromBody] BlockUserBody body,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var result = await mediator.Send(new BlockUserCommand(userId, body.BlockedUserId), cancellationToken);

        if (result.IsSuccess)
            LogUserBlocked(logger, userId);

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpDelete("block/{blockedUserId:guid}")]
    [DistributedRateLimit("unblock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Unblock(Guid blockedUserId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UnblockUserCommand(HttpContext.GetUserId(), blockedUserId), cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPost("report")]
    [DistributedRateLimit("reports")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Report(
        [FromBody] ReportUserBody body,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var command = new ReportUserCommand(userId, body.ReportedUserId, body.Reason, body.Details, body.CheerId);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogUserReported(logger, userId);

        return result.ToPayGateAwareResult(id => Ok(new { id }));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Friend request sent by user {UserId}")]
    private static partial void LogFriendRequestSent(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Friend request accepted by user {UserId}")]
    private static partial void LogFriendRequestAccepted(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Cheer sent by user {UserId}")]
    private static partial void LogCheerSent(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "User blocked by user {UserId}")]
    private static partial void LogUserBlocked(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "User reported by user {UserId}")]
    private static partial void LogUserReported(ILogger logger, Guid userId);
}
