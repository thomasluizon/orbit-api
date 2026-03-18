using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Support.Commands;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SupportController(IMediator mediator) : ControllerBase
{
    public record SupportRequest(string Name, string Email, string Subject, string Message);

    [HttpPost]
    public async Task<IActionResult> SendSupport(
        [FromBody] SupportRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SendSupportCommand(
            HttpContext.GetUserId(),
            request.Name,
            request.Email,
            request.Subject,
            request.Message);

        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(new { message = "Support request sent successfully" })
            : BadRequest(new { error = result.Error });
    }
}
