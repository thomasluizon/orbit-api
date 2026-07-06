using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Commands;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = AdminPolicy.Name)]
public partial class AdminController(IMediator mediator, ILogger<AdminController> logger) : ControllerBase
{
    public record BroadcastRequest(
        string SubjectEn,
        string SubjectPt,
        string BodyHtmlEn,
        string BodyHtmlPt,
        string? TestEmail);

    [HttpPost("marketing/broadcast")]
    [DistributedRateLimit("admin-broadcast")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SendMarketingBroadcast(
        [FromBody] BroadcastRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SendMarketingBroadcastCommand(
            request.SubjectEn, request.SubjectPt, request.BodyHtmlEn, request.BodyHtmlPt, request.TestEmail);

        var result = await mediator.Send(command, cancellationToken);
        if (result.IsFailure)
            return result.ToErrorResult();

        LogBroadcastRequested(logger, HttpContext.GetUserId(), result.Value.RecipientCount, result.Value.WasTest);
        return Accepted(new { recipientCount = result.Value.RecipientCount, test = result.Value.WasTest });
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Marketing broadcast requested by admin {UserId}: {RecipientCount} recipients, test={IsTest}")]
    private static partial void LogBroadcastRequested(ILogger logger, Guid userId, int recipientCount, bool isTest);
}
