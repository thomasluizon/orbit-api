using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Uploads.Commands;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/uploads")]
public partial class UploadsController(IMediator mediator, IObjectStorageService objectStorage, ILogger<UploadsController> logger) : ControllerBase
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

    [AllowAnonymous]
    [HttpGet("object/{userId}/{fileName}")]
    [DistributedRateLimit("upload-reads")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReadObject(string userId, string fileName, CancellationToken cancellationToken)
    {
        var objectKey = $"{userId}/{fileName}";
        if (!UploadObjectKeyPattern().IsMatch(objectKey))
            return NotFound();

        var readUrl = await objectStorage.CreateReadUrlAsync(objectKey, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Redirect(readUrl);
    }

    [GeneratedRegex(@"\A[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\.(?:png|jpg|webp)\z", RegexOptions.CultureInvariant)]
    private static partial Regex UploadObjectKeyPattern();

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Signed upload issued for user {UserId} content type {ContentType} size {SizeBytes}")]
    private static partial void LogUploadSigned(ILogger logger, Guid userId, string contentType, long sizeBytes);
}
