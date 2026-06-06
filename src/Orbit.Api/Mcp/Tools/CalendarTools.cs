using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// MCP calendar tools. <c>manage_calendar_sync</c> is a destructive mutation, so it routes through
/// <see cref="McpExecutorBridge"/> → <see cref="Orbit.Domain.Interfaces.IAgentOperationExecutor"/>
/// with <see cref="Orbit.Domain.Models.AgentExecutionSurface.Mcp"/> for shared policy evaluation
/// (confirmation gating, read-only-credential denial) and the <c>AgentAuditLogs</c> trail; it
/// forwards a confirmation token. The <c>get_calendar_events</c> read stays on MediatR.
/// </summary>
[McpServerToolType]
public class CalendarTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "get_calendar_events"), Description("Get upcoming Google Calendar events (next 60 days). Requires Google Calendar to be connected.")]
    public async Task<string> GetCalendarEvents(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
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

    [McpServerTool(Name = "manage_calendar_sync"), Description("Manage Google Calendar sync: set auto-sync on/off, dismiss an import, dismiss a suggestion, or run a sync now.")]
    public async Task<string> ManageCalendarSync(
        ClaimsPrincipal user,
        [Description("Action to perform: set_auto_sync, dismiss_import, dismiss_suggestion, or run_sync")] string action,
        [Description("For set_auto_sync: whether auto-sync should be enabled")] bool? enabled = null,
        [Description("For dismiss_suggestion: the suggestion ID (GUID)")] string? suggestionId = null,
        [Description("Confirmation token returned by confirm_agent_operation_v2 (required: calendar sync changes are destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "manage_calendar_sync", new
        {
            action,
            enabled,
            suggestion_id = suggestionId
        }, confirmationToken, cancellationToken);

        return result.Succeeded ? $"Calendar sync: {action} completed." : result.Message;
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
