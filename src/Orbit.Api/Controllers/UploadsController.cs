using Asp.Versioning;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Api.Uploads;
using Orbit.Application.Uploads.Commands;
using Orbit.Application.Uploads.Queries;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/uploads")]
public partial class UploadsController(IMediator mediator, IObjectStorageReadService objectReader, ILogger<UploadsController> logger) : ControllerBase
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

    [HttpGet("object/{userId}/{fileName}")]
    [DistributedRateLimit("upload-reads")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReadObject(string userId, string fileName, CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized();
        if (!Guid.TryParse(userId, out var ownerId) || ownerId != HttpContext.GetUserId())
            return NotFound();
        if (!UploadObjectKey.TryCreate(userId, fileName, out var objectKey))
            return NotFound();
        if (!await mediator.Send(new UploadOwnerExistsQuery(Guid.Parse(userId)), cancellationToken))
            return NotFound();
        var readUrl = await objectReader.CreateReadUrlAsync(objectKey, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Redirect(readUrl);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Signed upload issued for user {UserId} content type {ContentType} size {SizeBytes}")]
    private static partial void LogUploadSigned(ILogger logger, Guid userId, string contentType, long sizeBytes);
}
