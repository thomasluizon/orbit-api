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
    public record SetAiMemoryRequest(bool Enabled);
    public record SetAiSummaryRequest(bool Enabled);
    public record SetLanguageRequest(string Language);

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

    [HttpPut("ai-memory")]
    public async Task<IActionResult> SetAiMemory(
        [FromBody] SetAiMemoryRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetAiMemoryCommand(HttpContext.GetUserId(), request.Enabled);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("ai-summary")]
    public async Task<IActionResult> SetAiSummary(
        [FromBody] SetAiSummaryRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetAiSummaryCommand(HttpContext.GetUserId(), request.Enabled);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("language")]
    public async Task<IActionResult> SetLanguage(
        [FromBody] SetLanguageRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetLanguageCommand(HttpContext.GetUserId(), request.Language);
        var result = await mediator.Send(command, cancellationToken);

        return result.IsSuccess
            ? Ok()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("onboarding")]
    public async Task<IActionResult> CompleteOnboarding(CancellationToken cancellationToken)
    {
        var command = new CompleteOnboardingCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? Ok() : BadRequest(new { error = result.Error });
    }
}
