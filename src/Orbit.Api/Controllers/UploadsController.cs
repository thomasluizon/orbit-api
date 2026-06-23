using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Uploads.Commands;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/uploads")]
public class UploadsController(IMediator mediator) : ControllerBase
{
    public record SignUploadRequest(string Filename, string ContentType, long SizeBytes);

    [HttpPost("sign")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SignUpload(
        [FromBody] SignUploadRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SignUploadCommand(
            HttpContext.GetUserId(),
            request.Filename,
            request.ContentType,
            request.SizeBytes);
        var result = await mediator.Send(command, cancellationToken);

        return result.ToPayGateAwareResult(value => Ok(value));
    }
}
