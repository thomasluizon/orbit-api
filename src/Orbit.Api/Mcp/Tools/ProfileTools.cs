using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class ProfileTools(IMediator mediator)
{
    [McpServerTool(Name = "get_profile"), Description("Get the authenticated user's profile information.")]
    public async Task<string> GetProfile(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetProfileQuery(userId);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var p = result.Value;
        return $"Name: {p.Name}\n" +
               $"Email: {p.Email}\n" +
               $"Plan: {p.Plan}{(p.HasProAccess ? " (Pro)" : "")}\n" +
               (p.IsTrialActive ? $"Trial ends: {p.TrialEndsAt:yyyy-MM-dd}\n" : "") +
               (p.TimeZone is not null ? $"Timezone: {p.TimeZone}\n" : "") +
               (p.Language is not null ? $"Language: {p.Language}\n" : "") +
               $"AI Messages: {p.AiMessagesUsed}/{p.AiMessagesLimit}\n" +
               $"Level: {p.Level} ({p.LevelTitle}) - {p.TotalXp} XP\n" +
               $"Week starts on: {(p.WeekStartDay == 0 ? "Sunday" : "Monday")}";
    }

    [McpServerTool(Name = "set_timezone"), Description("Set the user's timezone.")]
    public async Task<string> SetTimezone(
        ClaimsPrincipal user,
        [Description("IANA timezone identifier (e.g., America/New_York, Europe/London)")] string timezone,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new SetTimezoneCommand(userId, timezone);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Timezone set to {timezone}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "set_language"), Description("Set the user's preferred language.")]
    public async Task<string> SetLanguage(
        ClaimsPrincipal user,
        [Description("Language code (e.g., en, pt-BR)")] string language,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new SetLanguageCommand(userId, language);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Language set to {language}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "set_ai_memory"), Description("Enable or disable AI memory (remembering user facts from conversations).")]
    public async Task<string> SetAiMemory(
        ClaimsPrincipal user,
        [Description("True to enable, false to disable")] bool enabled,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new SetAiMemoryCommand(userId, enabled);
        var result = await mediator.Send(command, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: {result.Error}";

        return enabled ? "AI memory enabled" : "AI memory disabled";
    }

    [McpServerTool(Name = "set_ai_summary"), Description("Enable or disable AI daily summary on the Today page.")]
    public async Task<string> SetAiSummary(
        ClaimsPrincipal user,
        [Description("True to enable, false to disable")] bool enabled,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new SetAiSummaryCommand(userId, enabled);
        var result = await mediator.Send(command, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: {result.Error}";

        return enabled ? "AI summary enabled" : "AI summary disabled";
    }

    [McpServerTool(Name = "set_color_scheme"), Description("Set the user's premium color scheme.")]
    public async Task<string> SetColorScheme(
        ClaimsPrincipal user,
        [Description("Color scheme key, or null/default to clear it")] string? colorScheme,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new SetColorSchemeCommand(userId, colorScheme);
        var result = await mediator.Send(command, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: {result.Error}";

        return $"Color scheme set to {colorScheme ?? "default"}";
    }

    [McpServerTool(Name = "set_week_start_day"), Description("Set which day the week starts on.")]
    public async Task<string> SetWeekStartDay(
        ClaimsPrincipal user,
        [Description("0 for Sunday, 1 for Monday")] int weekStartDay,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new SetWeekStartDayCommand(userId, weekStartDay);
        var result = await mediator.Send(command, cancellationToken);
        if (!result.IsSuccess)
            return $"Error: {result.Error}";

        return weekStartDay == 0 ? "Week start day set to Sunday" : "Week start day set to Monday";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        if (!Guid.TryParse(claim, out var userId))
            throw new UnauthorizedAccessException("User ID claim is not a valid GUID");
        return userId;
    }
}
