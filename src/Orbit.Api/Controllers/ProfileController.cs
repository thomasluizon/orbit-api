using Asp.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[ApiVersion(1.0)]
[ApiVersion(2.0)]
[Route("api/[controller]")]
public partial class ProfileController(
    IMediator mediator,
    ILogger<ProfileController> logger,
    IUserDateService userDateService) : ControllerBase
{
    public record SetTimezoneRequest(string TimeZone);
    public record SetNameRequest(string Name);
    public record SetAiSummaryRequest([property: JsonRequired] bool Enabled);
    public record SetProactiveAstraEnabledRequest([property: JsonRequired] bool Enabled);
    public record SetLanguageRequest(string Language);
    public record SetWeekStartDayRequest([property: JsonRequired] int WeekStartDay);
    public record SetClockFormatRequest([property: JsonRequired] bool Uses24HourClock);
    public record SetThemePreferenceRequest(string? ThemePreference);
    public record UpdateMarketingConsentRequest([property: JsonRequired] bool Enabled);
    public record ApplyOnboardingRequest(
        IReadOnlyList<ApplyHabitInput>? Habits,
        ApplyLogInput? FirstLog,
        ApplyGoalInput? Goal,
        int? WeekStartDay);

    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string ToggleLabel(bool enabled) => enabled ? "enabled" : "disabled";

    [HttpGet]
    [ProducesResponseType(typeof(ProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var query = new GetProfileQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);
        return result.ToPayGateAwareResult(v => Ok(v));
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

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogTimezoneChanged(request.TimeZone, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("name")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetName(
        [FromBody] SetNameRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetNameCommand(HttpContext.GetUserId(), request.Name);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogNameChanged(HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
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

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogAiSummaryToggled(ToggleLabel(request.Enabled), HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("proactive-astra")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetProactiveAstra(
        [FromBody] SetProactiveAstraEnabledRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetProactiveAstraEnabledCommand(HttpContext.GetUserId(), request.Enabled);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogProactiveAstraToggled(ToggleLabel(request.Enabled), HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
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

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogLanguageChanged(request.Language, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
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

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogWeekStartDayChanged(request.WeekStartDay, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("clock-format")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetClockFormat(
        [FromBody] SetClockFormatRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetClockFormatCommand(HttpContext.GetUserId(), request.Uses24HourClock);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogClockFormatChanged(request.Uses24HourClock ? "24-hour" : "12-hour", HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
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

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogThemePreferenceChanged(request.ThemePreference, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("onboarding")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompleteOnboarding(CancellationToken cancellationToken)
    {
        var command = new CompleteOnboardingCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPost("onboarding/apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ApplyOnboarding(
        [FromBody] ApplyOnboardingRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ApplyOnboardingCommand(
            HttpContext.GetUserId(),
            request.Habits ?? [],
            request.FirstLog,
            request.Goal,
            request.WeekStartDay);
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(value => Ok(value));
    }

    [HttpPut("import-prompt/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DismissImportPrompt(CancellationToken cancellationToken)
    {
        var command = new DismissImportPromptCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("tour")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompleteTour(CancellationToken cancellationToken)
    {
        var command = new CompleteTourCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpDelete("tour")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ResetTour(CancellationToken cancellationToken)
    {
        var command = new ResetTourCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);
        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("marketing-consent")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateMarketingConsent(
        [FromBody] UpdateMarketingConsentRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateMarketingConsentCommand(HttpContext.GetUserId(), request.Enabled);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogMarketingConsentChanged(ToggleLabel(request.Enabled), HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPost("reset")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ResetAccount(CancellationToken cancellationToken)
    {
        var command = new ResetAccountCommand(HttpContext.GetUserId());
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
            logger.LogAccountReset(HttpContext.GetUserId());

        return result.ToPayGateAwareResult();
    }

    [HttpGet("export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportUserData(CancellationToken cancellationToken)
    {
        var query = new ExportUserDataQuery(HttpContext.GetUserId());
        var result = await mediator.Send(query, cancellationToken);

        if (!result.IsSuccess)
            return result.ToErrorResult();

        var userToday = await userDateService.GetUserTodayAsync(HttpContext.GetUserId(), cancellationToken);
        var fileName = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"orbit-data-export-{userToday:yyyy-MM-dd}.json");
        var json = JsonSerializer.SerializeToUtf8Bytes(result.Value, ExportJsonOptions);
        return File(json, "application/json", fileName);
    }
}

internal static partial class ProfileControllerLogging
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Timezone changed to {Timezone} for user {UserId}")]
    internal static partial void LogTimezoneChanged(this ILogger logger, string timezone, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "AI summary {State} for user {UserId}")]
    internal static partial void LogAiSummaryToggled(this ILogger logger, string state, Guid userId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Language changed to {Language} for user {UserId}")]
    internal static partial void LogLanguageChanged(this ILogger logger, string language, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Week start day changed to {WeekStartDay} for user {UserId}")]
    internal static partial void LogWeekStartDayChanged(this ILogger logger, int weekStartDay, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Theme preference changed to {ThemePreference} for user {UserId}")]
    internal static partial void LogThemePreferenceChanged(this ILogger logger, string? themePreference, Guid userId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Account reset for user {UserId}")]
    internal static partial void LogAccountReset(this ILogger logger, Guid userId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Display name changed for user {UserId}")]
    internal static partial void LogNameChanged(this ILogger logger, Guid userId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "Marketing email consent {State} for user {UserId}")]
    internal static partial void LogMarketingConsentChanged(this ILogger logger, string state, Guid userId);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information, Message = "Clock format changed to {Format} for user {UserId}")]
    internal static partial void LogClockFormatChanged(this ILogger logger, string format, Guid userId);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "Proactive Astra check-ins {State} for user {UserId}")]
    internal static partial void LogProactiveAstraToggled(this ILogger logger, string state, Guid userId);
}
