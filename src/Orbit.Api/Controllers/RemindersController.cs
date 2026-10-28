using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Habits.Commands;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/reminders")]
public sealed class RemindersController(IMediator mediator) : ControllerBase
{
    public sealed record DoneRequest(string Token);

    [AllowAnonymous]
    [HttpPost("done")]
    [DistributedRateLimit("auth")]
    [ProducesResponseType<LogHabitResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Done(DoneRequest request, CancellationToken cancellationToken)
    {
        Guid? caller = User.Identity?.IsAuthenticated == true ? HttpContext.GetUserId() : null;
        var result = await mediator.Send(new CompleteReminderCommand(request.Token, caller), cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }
}
