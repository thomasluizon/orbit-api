using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Profile.Queries;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class ProfileTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "get_profile"), Description("Get the authenticated user's profile information.")]
    public async Task<CallToolResult> GetProfile(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var query = new GetProfileQuery(userId);
            var result = await mediator.Send(query, cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var p = result.Value;
            return McpToolHelpers.Text($"Name: {p.Name}\n" + $"Email: {p.Email}\n" + $"Plan: {p.Plan}{(p.HasProAccess ? " (Pro)" : "")}\n" + (p.IsTrialActive ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Trial ends: {p.TrialEndsAt:yyyy-MM-dd}\n") : "") + (p.TimeZone is not null ? $"Timezone: {p.TimeZone}\n" : "") + (p.Language is not null ? $"Language: {p.Language}\n" : "") + $"AI Messages ({p.AiMessagesPeriod}): {p.AiMessagesUsed}/{p.AiMessagesLimit}\n" + $"AI Messages reset: {p.AiMessagesResetsAt:O}\n" + $"Level: {p.Level} ({p.LevelTitle}) - {p.TotalXp} XP\n" + $"Week starts on: {(p.WeekStartDay == 0 ? "Sunday" : "Monday")}\n" + $"Clock: {(p.Uses24HourClock ? "24-hour" : "12-hour")}");
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "set_name"), Description("Set the user's display name.")]
    public async Task<CallToolResult> SetName(
        ClaimsPrincipal user,
        [Description("Display name")] string name,
        [Description("Confirmation token if requested by the executor")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "update_profile_preferences", new { action = "set_name", name }, confirmationToken, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Name set to {name}") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "set_timezone"), Description("Set the user's timezone.")]
    public async Task<CallToolResult> SetTimezone(
        ClaimsPrincipal user,
        [Description("IANA timezone identifier (e.g., America/New_York, Europe/London)")] string timezone,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "update_profile_preferences", new { action = "set_timezone", timezone }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Timezone set to {timezone}") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "set_language"), Description("Set the user's preferred language.")]
    public async Task<CallToolResult> SetLanguage(
        ClaimsPrincipal user,
        [Description("Language code (e.g., en, pt-BR)")] string language,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "update_profile_preferences", new { action = "set_language", language }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Language set to {language}") : McpToolHelpers.Error(result.Message);
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "set_ai_summary"), Description("Enable or disable AI daily summary on the Today page.")]
    public async Task<CallToolResult> SetAiSummary(
        ClaimsPrincipal user,
        [Description("True to enable, false to disable")] bool enabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "set_ai_summary", new { enabled }, confirmationToken: null, cancellationToken);
            if (!result.Succeeded)
                return McpToolHelpers.Error(result.Message);
            return enabled ? McpToolHelpers.Text("AI summary enabled") : McpToolHelpers.Text("AI summary disabled");
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "set_proactive_astra"), Description("Enable or disable proactive push check-ins. Requires Pro; in-app items remain available.")]
    public async Task<CallToolResult> SetProactiveAstra(
        ClaimsPrincipal user,
        [Description("True to enable, false to disable")] bool enabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "set_proactive_astra", new { enabled }, confirmationToken: null, cancellationToken);
            if (!result.Succeeded)
                return McpToolHelpers.Error(result.Message);
            return enabled ? McpToolHelpers.Text("Proactive check-ins enabled") : McpToolHelpers.Text("Proactive check-ins disabled");
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "set_week_start_day"), Description("Set which day the week starts on.")]
    public async Task<CallToolResult> SetWeekStartDay(
        ClaimsPrincipal user,
        [Description("0 for Sunday, 1 for Monday")] int weekStartDay,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "update_profile_preferences", new { action = "set_week_start_day", week_start_day = weekStartDay }, confirmationToken: null, cancellationToken);
            if (!result.Succeeded)
                return McpToolHelpers.Error(result.Message);
            return weekStartDay == 0 ? McpToolHelpers.Text("Week start day set to Sunday") : McpToolHelpers.Text("Week start day set to Monday");
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "set_clock_format"), Description("Set the user's clock format.")]
    public async Task<CallToolResult> SetClockFormat(
        ClaimsPrincipal user,
        [Description("True for a 24-hour clock, false for a 12-hour clock")] bool uses24HourClock,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "update_profile_preferences", new { action = "set_clock_format", uses_24_hour_clock = uses24HourClock }, confirmationToken: null, cancellationToken);
            if (!result.Succeeded)
                return McpToolHelpers.Error(result.Message);
            return uses24HourClock ? McpToolHelpers.Text("Clock set to 24-hour") : McpToolHelpers.Text("Clock set to 12-hour");
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }
}
