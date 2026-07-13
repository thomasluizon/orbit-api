using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Uploads.Commands;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/uploads")]
public partial class UploadsController(IMediator mediator, ILogger<UploadsController> logger) : ControllerBase
{
    public record SignUploadRequest(string ContentType, [property: JsonRequired] long SizeBytes);

    [HttpPost("sign")]
    [DistributedRateLimit("uploads")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SignUpload(
        [FromBody] SignUploadRequest request,
        CancellationToken cancellationToken)
    {
        var userId = HttpContext.GetUserId();
        var command = new SignUploadCommand(userId, request.ContentType, request.SizeBytes);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogUploadSigned(logger, userId, request.ContentType, request.SizeBytes);

        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Signed upload issued for user {UserId} content type {ContentType} size {SizeBytes}")]
    private static partial void LogUploadSigned(ILogger logger, Guid userId, string contentType, long sizeBytes);
}
