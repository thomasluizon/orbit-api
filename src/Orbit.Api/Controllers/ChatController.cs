using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Chat.Commands;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ChatController(IMediator mediator) : ControllerBase
{
    public record ChatRequest(string Message);

    [HttpPost]
    public async Task<IActionResult> ProcessChat(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ProcessUserChatCommand(HttpContext.GetUserId(), request.Message);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }
}
