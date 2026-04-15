using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class CalendarTools(IMediator mediator)
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

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        if (!Guid.TryParse(claim, out var userId))
            throw new UnauthorizedAccessException("User ID claim is not a valid GUID");
        return userId;
    }
}
