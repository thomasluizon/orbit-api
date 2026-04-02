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
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var query = new GetProfileQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error });
    }

    [HttpPut("timezone")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetTimezone(
        [FromBody] SetTimezoneRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetTimezoneCommand(HttpContext.GetUserId(), request.TimeZone);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            logger.LogInformation("Timezone changed to {Timezone} for user {UserId}", request.TimeZone, HttpContext.GetUserId());

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("ai-memory")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetAiMemory(
        [FromBody] SetAiMemoryRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetAiMemoryCommand(HttpContext.GetUserId(), request.Enabled);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            logger.LogInformation("AI memory {State} for user {UserId}", request.Enabled ? "enabled" : "disabled", HttpContext.GetUserId());

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("ai-summary")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetAiSummary(
        [FromBody] SetAiSummaryRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetAiSummaryCommand(HttpContext.GetUserId(), request.Enabled);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            logger.LogInformation("AI summary {State} for user {UserId}", request.Enabled ? "enabled" : "disabled", HttpContext.GetUserId());

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("language")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetLanguage(
        [FromBody] SetLanguageRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetLanguageCommand(HttpContext.GetUserId(), request.Language);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            logger.LogInformation("Language changed to {Language} for user {UserId}", request.Language, HttpContext.GetUserId());

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("week-start-day")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetWeekStartDay(
        [FromBody] SetWeekStartDayRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetWeekStartDayCommand(HttpContext.GetUserId(), request.WeekStartDay);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            logger.LogInformation("Week start day changed to {WeekStartDay} for user {UserId}", request.WeekStartDay, HttpContext.GetUserId());

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("theme-preference")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetThemePreference(
        [FromBody] SetThemePreferenceRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetThemePreferenceCommand(HttpContext.GetUserId(), request.ThemePreference);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            logger.LogInformation("Theme preference changed to {ThemePreference} for user {UserId}", request.ThemePreference, HttpContext.GetUserId());

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("color-scheme")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetColorScheme(
        [FromBody] SetColorSchemeRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetColorSchemeCommand(HttpContext.GetUserId(), request.ColorScheme);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            logger.LogInformation("Color scheme changed to {ColorScheme} for user {UserId}", request.ColorScheme, HttpContext.GetUserId());

        return result.IsSuccess
            ? NoContent()
            : BadRequest(new { error = result.Error });
    }

    [HttpPut("onboarding")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompleteOnboarding(CancellationToken cancellationToken)
    {
        var command = new CompleteOnboardingCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error });
    }

    [HttpPost("reset")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ResetAccount(CancellationToken cancellationToken)
    {
        var command = new ResetAccountCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            logger.LogInformation("Account reset for user {UserId}", HttpContext.GetUserId());

        return result.IsSuccess
            ? Ok()
            : BadRequest(new { error = result.Error });
    }

}
