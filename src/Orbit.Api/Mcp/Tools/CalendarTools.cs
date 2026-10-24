using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Orbit.Application.Chat.Tools.Implementations;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class CalendarTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "get_calendar_events"), Description("Get upcoming Google Calendar events (next 60 days). Requires Google Calendar to be connected.")]
    public async Task<string> GetCalendarEvents(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = McpToolHelpers.GetUserId(user);
        var query = new GetCalendarEventsQuery(userId);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var events = result.Value;
        if (events.Count == 0)
            return "No upcoming calendar events found.";

        var lines = events.Select(e =>
            $"- {e.Title}" +
            (e.StartDate is not null ? $" | {e.StartDate}" : "") +
            (e.StartTime is not null ? $" {e.StartTime}" : "") +
            (e.EndTime is not null ? $"-{e.EndTime}" : "") +
            (e.IsRecurring ? " | recurring" : "") +
            (e.Reminders.Count > 0 ? $" | reminders: {string.Join(",", e.Reminders)}min" : ""));

        return $"Calendar Events ({events.Count}):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "get_calendar_overview"), Description("Read calendar events, selected calendars, auto-sync state, and sync suggestions.")]
    public async Task<string> GetCalendarOverview(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var tool = new GetCalendarOverviewTool(mediator);
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }),
            McpToolHelpers.GetUserId(user), cancellationToken);
        return result.Success ? JsonSerializer.Serialize(result.Payload) : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "set_selected_calendars"), Description("Select Google Calendars to sync. An empty list resets to all owned calendars.")]
    public async Task<string> SetSelectedCalendars(
        ClaimsPrincipal user,
        [Description("Calendar IDs to sync; an empty list resets to all owned calendars")] string[] calendarIds,
        [Description("Confirmation token returned by confirm_agent_operation_v2 through the Claude connector; manual API keys must use the Claude connector or make this change in the Orbit app.")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "manage_calendar_sync", new
        {
            action = "set_selected_calendars",
            calendar_ids = calendarIds
        }, confirmationToken, cancellationToken);
        return result.Succeeded ? "Calendar selection updated." : result.Message;
    }

    [McpServerTool(Name = "manage_calendar_sync"), Description("Manage Google Calendar sync: set auto-sync on/off, dismiss an import, dismiss a suggestion, run a sync now, or select calendars.")]
    public async Task<string> ManageCalendarSync(
        ClaimsPrincipal user,
        [Description("Action to perform: set_auto_sync, dismiss_import, dismiss_suggestion, run_sync, or set_selected_calendars")] string action,
        [Description("For set_auto_sync: whether auto-sync should be enabled")] bool? enabled = null,
        [Description("For dismiss_suggestion: the suggestion ID (GUID)")] string? suggestionId = null,
        [Description("Confirmation token returned by confirm_agent_operation_v2 through the Claude connector; manual API keys must use the Claude connector or make this change in the Orbit app. (required: calendar sync changes are destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default,
        [Description("For set_selected_calendars: calendar IDs; empty resets to all owned calendars")] string[]? calendarIds = null)
    {
        var result = await executorBridge.ExecuteAsync(user, "manage_calendar_sync", new
        {
            action,
            calendar_ids = calendarIds,
            enabled,
            suggestion_id = suggestionId
        }, confirmationToken, cancellationToken);

        return result.Succeeded ? $"Calendar sync: {action} completed." : result.Message;
    }
}
