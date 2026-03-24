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
public class ProfileController(IMediator mediator, ILogger<ProfileController> logger) : ControllerBase
{
    public record SetTimezoneRequest(string TimeZone);
    public record SetAiMemoryRequest(bool Enabled);
    public record SetAiSummaryRequest(bool Enabled);
    public record SetLanguageRequest(string Language);
    public record SetWeekStartDayRequest(int WeekStartDay);

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

        if (result.IsSuccess)
            logger.LogInformation("Timezone changed to {Timezone} for user {UserId}", request.TimeZone, HttpContext.GetUserId());

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

        if (result.IsSuccess)
            logger.LogInformation("Language changed to {Language} for user {UserId}", request.Language, HttpContext.GetUserId());

        return result.IsSuccess
            ? Ok()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("week-start-day")]
    public async Task<IActionResult> SetWeekStartDay(
        [FromBody] SetWeekStartDayRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetWeekStartDayCommand(HttpContext.GetUserId(), request.WeekStartDay);
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

    [HttpPut("missions/dismiss")]
    public async Task<IActionResult> DismissMissions(CancellationToken cancellationToken)
    {
        var command = new DismissMissionsCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? Ok() : BadRequest(new { error = result.Error });
    }

    public record MarkTourRequest(string PageName);

    [HttpPut("tour")]
    public async Task<IActionResult> MarkTourCompleted(
        [FromBody] MarkTourRequest request,
        CancellationToken cancellationToken)
    {
        var command = new MarkTourCompletedCommand(HttpContext.GetUserId(), request.PageName);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? Ok() : BadRequest(new { error = result.Error });
    }
}
