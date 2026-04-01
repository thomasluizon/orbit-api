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
    public record SetThemePreferenceRequest(string? ThemePreference);
    public record SetColorSchemeRequest(string? ColorScheme);

    [HttpGet]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var query = new GetProfileQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error });
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

        if (result.IsSuccess)
            logger.LogInformation("AI memory {State} for user {UserId}", request.Enabled ? "enabled" : "disabled", HttpContext.GetUserId());

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

        if (result.IsSuccess)
            logger.LogInformation("AI summary {State} for user {UserId}", request.Enabled ? "enabled" : "disabled", HttpContext.GetUserId());

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

        if (result.IsSuccess)
            logger.LogInformation("Week start day changed to {WeekStartDay} for user {UserId}", request.WeekStartDay, HttpContext.GetUserId());

        return result.IsSuccess
            ? Ok()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("theme-preference")]
    public async Task<IActionResult> SetThemePreference(
        [FromBody] SetThemePreferenceRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetThemePreferenceCommand(HttpContext.GetUserId(), request.ThemePreference);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            logger.LogInformation("Theme preference changed to {ThemePreference} for user {UserId}", request.ThemePreference, HttpContext.GetUserId());

        return result.IsSuccess
            ? Ok()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("color-scheme")]
    public async Task<IActionResult> SetColorScheme(
        [FromBody] SetColorSchemeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetColorSchemeCommand(HttpContext.GetUserId(), request.ColorScheme);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            logger.LogInformation("Color scheme changed to {ColorScheme} for user {UserId}", request.ColorScheme, HttpContext.GetUserId());

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
