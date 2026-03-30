using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class GoalTools(IMediator mediator)
{
    [McpServerTool(Name = "list_goals"), Description("List all goals for the authenticated user.")]
    public async Task<string> ListGoals(
        ClaimsPrincipal user,
        [Description("Filter by status: Active, Completed, or Abandoned")] string? status = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        Domain.Enums.GoalStatus? statusFilter = status is not null
            ? Enum.Parse<Domain.Enums.GoalStatus>(status, true)
            : null;

        var result = await mediator.Send(new GetGoalsQuery(userId, statusFilter), cancellationToken);

        if (result.Items.Count == 0)
            return "No goals found.";

        var lines = result.Items.Select(g =>
            $"- {g.Title} (id: {g.Id}) | {g.CurrentValue}/{g.TargetValue} {g.Unit} ({g.ProgressPercentage:F0}%)" +
            $" | Status: {g.Status}" +
            (g.Deadline is not null ? $" | Deadline: {g.Deadline}" : "") +
            (g.TrackingStatus is not null ? $" | Tracking: {g.TrackingStatus}" : ""));

        return $"Goals ({result.TotalCount}):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "create_goal"), Description("Create a new goal. Requires Pro subscription.")]
    public async Task<string> CreateGoal(
        ClaimsPrincipal user,
        [Description("Goal title")] string title,
        [Description("Target value to reach")] decimal targetValue,
        [Description("Unit of measurement (e.g., km, books, hours)")] string unit,
        [Description("Optional description")] string? description = null,
        [Description("Optional deadline in YYYY-MM-DD format")] string? deadline = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new CreateGoalCommand(
            userId,
            title,
            description,
            targetValue,
            unit,
            deadline is not null ? DateOnly.Parse(deadline) : null);

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Created goal '{title}' (id: {result.Value})"
            : $"Error: {result.Error}";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        return Guid.Parse(claim);
    }
}
