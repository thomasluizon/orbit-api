using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Application.Email.Commands;

namespace Orbit.Api.Controllers;

[ApiController]
[Route("api/email")]
[AllowAnonymous]
public sealed class EmailController(ISender sender) : ControllerBase
{
    [HttpPost("ses-events")]
    [Consumes("text/plain", "application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SesEvents(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        return await sender.Send(new ProcessSesEventCommand(payload), cancellationToken) ? Ok() : BadRequest();
    }
}
