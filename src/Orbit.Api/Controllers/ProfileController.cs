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

#pragma warning disable CA1873

namespace Orbit.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class ProfileController(
    IMediator mediator,
    ILogger<ProfileController> logger,
    IUserDateService userDateService) : ControllerBase
{
    public record SetTimezoneRequest(string TimeZone);
    public record SetNameRequest(string Name);
    public record SetAiMemoryRequest([property: JsonRequired] bool Enabled);
    public record SetAiSummaryRequest([property: JsonRequired] bool Enabled);
    public record SetProactiveAstraEnabledRequest([property: JsonRequired] bool Enabled);
    public record SetLanguageRequest(string Language);
    public record SetWeekStartDayRequest([property: JsonRequired] int WeekStartDay);
    public record SetThemePreferenceRequest(string? ThemePreference);
    public record SetColorSchemeRequest(string? ColorScheme);
    public record SetHandleRequest(string Handle);
    public record SetSocialOptInRequest([property: JsonRequired] bool Enabled);
    public record UpdateMarketingConsentRequest([property: JsonRequired] bool Enabled);
    public record UpdatePublicProfileRequest(
        [property: JsonRequired] bool Enabled,
        [property: JsonRequired] bool ShowStreak,
        [property: JsonRequired] bool ShowLevel,
        [property: JsonRequired] bool ShowAchievements,
        [property: JsonRequired] bool ShowTopHabits,
        [property: JsonRequired] bool Regenerate);

    public record ApplyOnboardingRequest(
        IReadOnlyList<ApplyHabitInput>? Habits,
        ApplyLogInput? FirstLog,
        ApplyGoalInput? Goal,
        int? WeekStartDay,
        string? ColorScheme);

    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string ToggleLabel(bool enabled) => enabled ? "enabled" : "disabled";

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
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

        if (result.IsSuccess)
            LogTimezoneChanged(logger, request.TimeZone, HttpContext.GetUserId());

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

        if (result.IsSuccess)
            LogNameChanged(logger, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
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
            LogAiMemoryToggled(logger, ToggleLabel(request.Enabled), HttpContext.GetUserId());

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

        if (result.IsSuccess)
            LogAiSummaryToggled(logger, ToggleLabel(request.Enabled), HttpContext.GetUserId());

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

        if (result.IsSuccess)
            LogProactiveAstraToggled(logger, ToggleLabel(request.Enabled), HttpContext.GetUserId());

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

        if (result.IsSuccess)
            LogLanguageChanged(logger, request.Language, HttpContext.GetUserId());

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

        if (result.IsSuccess)
            LogWeekStartDayChanged(logger, request.WeekStartDay, HttpContext.GetUserId());

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

        if (result.IsSuccess)
            LogThemePreferenceChanged(logger, request.ThemePreference, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
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
            LogColorSchemeChanged(logger, request.ColorScheme, HttpContext.GetUserId());

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
            request.WeekStartDay,
            request.ColorScheme);
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

    [HttpPut("handle")]
    [DistributedRateLimit("set-handle")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetHandle(
        [FromBody] SetHandleRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetHandleCommand(HttpContext.GetUserId(), request.Handle);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogHandleChanged(logger, HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("social-opt-in")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetSocialOptIn(
        [FromBody] SetSocialOptInRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SetSocialOptInCommand(HttpContext.GetUserId(), request.Enabled);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogSocialOptInChanged(logger, ToggleLabel(request.Enabled), HttpContext.GetUserId());

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

        if (result.IsSuccess)
            LogMarketingConsentChanged(logger, ToggleLabel(request.Enabled), HttpContext.GetUserId());

        return result.ToPayGateAwareResult(() => NoContent());
    }

    [HttpPut("public")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdatePublicProfile(
        [FromBody] UpdatePublicProfileRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdatePublicProfileCommand(
            HttpContext.GetUserId(),
            request.Enabled,
            request.ShowStreak,
            request.ShowLevel,
            request.ShowAchievements,
            request.ShowTopHabits,
            request.Regenerate);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsSuccess)
            LogPublicProfileUpdated(logger, ToggleLabel(request.Enabled), HttpContext.GetUserId());

        return result.ToPayGateAwareResult(value => Ok(value));
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
            LogAccountReset(logger, HttpContext.GetUserId());

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
        var fileName = $"orbit-data-export-{userToday:yyyy-MM-dd}.json";
        var json = JsonSerializer.SerializeToUtf8Bytes(result.Value, ExportJsonOptions);
        return File(json, "application/json", fileName);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Timezone changed to {Timezone} for user {UserId}")]
    private static partial void LogTimezoneChanged(ILogger logger, string timezone, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "AI memory {State} for user {UserId}")]
    private static partial void LogAiMemoryToggled(ILogger logger, string state, Guid userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "AI summary {State} for user {UserId}")]
    private static partial void LogAiSummaryToggled(ILogger logger, string state, Guid userId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Language changed to {Language} for user {UserId}")]
    private static partial void LogLanguageChanged(ILogger logger, string language, Guid userId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Week start day changed to {WeekStartDay} for user {UserId}")]
    private static partial void LogWeekStartDayChanged(ILogger logger, int weekStartDay, Guid userId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Theme preference changed to {ThemePreference} for user {UserId}")]
    private static partial void LogThemePreferenceChanged(ILogger logger, string? themePreference, Guid userId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Color scheme changed to {ColorScheme} for user {UserId}")]
    private static partial void LogColorSchemeChanged(ILogger logger, string? colorScheme, Guid userId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Account reset for user {UserId}")]
    private static partial void LogAccountReset(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Display name changed for user {UserId}")]
    private static partial void LogNameChanged(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Handle changed for user {UserId}")]
    private static partial void LogHandleChanged(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Social opt-in {State} for user {UserId}")]
    private static partial void LogSocialOptInChanged(ILogger logger, string state, Guid userId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Public profile {State} for user {UserId}")]
    private static partial void LogPublicProfileUpdated(ILogger logger, string state, Guid userId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "Marketing email consent {State} for user {UserId}")]
    private static partial void LogMarketingConsentChanged(ILogger logger, string state, Guid userId);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "Proactive Astra check-ins {State} for user {UserId}")]
    private static partial void LogProactiveAstraToggled(ILogger logger, string state, Guid userId);

}
