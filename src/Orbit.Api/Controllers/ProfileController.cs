using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Queries;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ProfileController(IMediator mediator) : ControllerBase
{
    public record SetTimezoneRequest(string TimeZone);

    [HttpGet]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var query = new GetProfileQuery(HttpContext.GetUserId());
        var profile = await mediator.Send(query, cancellationToken);
        return Ok(profile);
    }

    [HttpPut("timezone")]
    public async Task<IActionResult> SetTimezone(
        [FromBody] SetTimezoneRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetTimezoneCommand(HttpContext.GetUserId(), request.TimeZone);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok()
            : BadRequest(new { error = result.Error });
    }
}
