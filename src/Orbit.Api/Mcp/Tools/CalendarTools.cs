using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Orbit.Application.Chat.Tools.Implementations;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Calendar.Queries;
using ModelContextProtocol.Protocol;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class CalendarTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "get_calendar_events"), Description("Get upcoming Google Calendar events (next 60 days). Requires Google Calendar to be connected.")]
    public async Task<CallToolResult> GetCalendarEvents(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var query = new GetCalendarEventsQuery(userId);
            var result = await mediator.Send(query, cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var events = result.Value;
            if (events.Count == 0)
                return McpToolHelpers.Text("No upcoming calendar events found.");
            var lines = events.Select(e => $"- {e.Title}" + (e.StartDate is not null ? $" | {e.StartDate}" : "") + (e.StartTime is not null ? $" {e.StartTime}" : "") + (e.EndTime is not null ? $"-{e.EndTime}" : "") + (e.IsRecurring ? " | recurring" : "") + (e.Reminders.Count > 0 ? $" | reminders: {string.Join(",", e.Reminders)}min" : ""));
            return McpToolHelpers.Text($"Calendar Events ({events.Count}):\n{string.Join("\n", lines)}");
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

    [McpServerTool(Name = "get_calendar_overview"), Description("Read calendar events, selected calendars, auto-sync state, and sync suggestions.")]
    public async Task<CallToolResult> GetCalendarOverview(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        try
        {
            var tool = new GetCalendarOverviewTool(mediator);
            var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), McpToolHelpers.GetUserId(user), cancellationToken);
            return result.Success ? McpToolHelpers.Text(JsonSerializer.Serialize(result.Payload)) : McpToolHelpers.Error($"Error: {result.Error}");
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

    [McpServerTool(Name = "set_selected_calendars"), Description("Select Google Calendars to sync. An empty list resets to all owned calendars.")]
    public async Task<CallToolResult> SetSelectedCalendars(
        ClaimsPrincipal user,
        [Description("Calendar IDs to sync; an empty list resets to all owned calendars")] string[] calendarIds,
        [Description("Confirmation token returned by confirm_agent_operation_v2 through the Claude connector; manual API keys must use the Claude connector or make this change in the Orbit app.")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "manage_calendar_sync", new { action = "set_selected_calendars", calendar_ids = calendarIds }, confirmationToken, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text("Calendar selection updated.") : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "manage_calendar_sync"), Description("Manage Google Calendar sync: set auto-sync on/off, dismiss an import, dismiss a suggestion, run a sync now, or select calendars.")]
    public async Task<CallToolResult> ManageCalendarSync(
        ClaimsPrincipal user,
        [Description("Action to perform: set_auto_sync, dismiss_import, dismiss_suggestion, run_sync, or set_selected_calendars")] string action,
        [Description("For set_auto_sync: whether auto-sync should be enabled")] bool? enabled = null,
        [Description("For dismiss_suggestion: the suggestion ID (GUID)")] string? suggestionId = null,
        [Description("Confirmation token returned by confirm_agent_operation_v2 through the Claude connector; manual API keys must use the Claude connector or make this change in the Orbit app. (required: calendar sync changes are destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default,
        [Description("For set_selected_calendars: calendar IDs; empty resets to all owned calendars")] string[]? calendarIds = null)
    {
        try
        {
            _ = McpInputParser.ParseOptionalGuid(suggestionId, "suggestionId");
            var result = await executorBridge.ExecuteAsync(user, "manage_calendar_sync", new { action, calendar_ids = calendarIds, enabled, suggestion_id = suggestionId }, confirmationToken, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Calendar sync: {action} completed.") : McpToolHelpers.Error(result.Message);
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
