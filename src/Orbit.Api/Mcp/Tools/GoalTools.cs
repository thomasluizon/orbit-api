using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Enums;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class GoalTools(IMediator mediator)
{
    private static readonly System.Text.Json.JsonSerializerOptions CaseInsensitiveJsonOptions = new() { PropertyNameCaseInsensitive = true };
    [McpServerTool(Name = "list_goals"), Description("List all goals for the authenticated user.")]
    public async Task<string> ListGoals(
        ClaimsPrincipal user,
        [Description("Filter by status: Active, Completed, or Abandoned")] string? status = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        GoalStatus? statusFilter = status is not null
            ? Enum.Parse<GoalStatus>(status, true)
            : null;

        var result = await mediator.Send(new GetGoalsQuery(userId, statusFilter), cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var goals = result.Value;

        if (goals.Items.Count == 0)
            return "No goals found.";

        var lines = goals.Items.Select(g =>
            $"- {g.Title} (id: {g.Id}) | {g.CurrentValue}/{g.TargetValue} {g.Unit} ({g.ProgressPercentage:F0}%)" +
            $" | Status: {g.Status}" +
            (g.Deadline is not null ? $" | Deadline: {g.Deadline}" : "") +
            (g.TrackingStatus is not null ? $" | Tracking: {g.TrackingStatus}" : ""));

        return $"Goals ({goals.TotalCount}):\n{string.Join("\n", lines)}";
    }

    [McpServerTool(Name = "create_goal"), Description("Create a new goal. Requires Pro subscription.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK requires individual [Description]-annotated parameters for tool schema generation")]
    public async Task<string> CreateGoal(
        ClaimsPrincipal user,
        [Description("Goal title")] string title,
        [Description("Target value to reach")] decimal targetValue,
        [Description("Unit of measurement (e.g., km, books, hours)")] string unit,
        [Description("Optional description")] string? description = null,
        [Description("Optional deadline in YYYY-MM-DD format")] string? deadline = null,
        [Description("Goal type: Standard (default) or Streak")] string type = "Standard",
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var goalType = Enum.TryParse<GoalType>(type, ignoreCase: true, out var parsedType)
            ? parsedType
            : GoalType.Standard;

        var command = new CreateGoalCommand(
            userId,
            title,
            description,
            targetValue,
            unit,
            McpInputParser.ParseOptionalDate(deadline, "deadline"),
            Type: goalType);

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Created goal '{title}' (id: {result.Value})"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "get_goal"), Description("Get detailed information about a specific goal by ID, including progress history and linked habits.")]
    public async Task<string> GetGoal(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetGoalByIdQuery(userId, McpInputParser.ParseGuid(goalId, "goalId"));
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var g = result.Value;
        var info = $"Title: {g.Title}\nID: {g.Id}\n" +
                   $"Progress: {g.CurrentValue}/{g.TargetValue} {g.Unit} ({g.ProgressPercentage:F1}%)\n" +
                   $"Status: {g.Status}\n" +
                   (g.Description is not null ? $"Description: {g.Description}\n" : "") +
                   (g.Deadline is not null ? $"Deadline: {g.Deadline}\n" : "") +
                   $"Created: {g.CreatedAtUtc:yyyy-MM-dd}\n" +
                   (g.CompletedAtUtc is not null ? $"Completed: {g.CompletedAtUtc:yyyy-MM-dd}\n" : "") +
                   (g.LinkedHabits.Count > 0 ? $"Linked habits: {string.Join(", ", g.LinkedHabits.Select(h => $"{h.Title} ({h.Id})"))}\n" : "") +
                   (g.ProgressHistory.Count > 0 ? $"Recent progress: {string.Join(", ", g.ProgressHistory.Take(5).Select(p => $"{p.PreviousValue}->{p.Value}"))}\n" : "");
        return info;
    }

    [McpServerTool(Name = "update_goal"), Description("Update a goal's title, description, target value, unit, or deadline.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "MCP SDK requires individual [Description]-annotated parameters for tool schema generation")]
    public async Task<string> UpdateGoal(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("Goal title")] string title,
        [Description("Target value")] decimal targetValue,
        [Description("Unit of measurement")] string unit,
        [Description("Optional description")] string? description = null,
        [Description("Optional deadline in YYYY-MM-DD format")] string? deadline = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new UpdateGoalCommand(
            userId,
            McpInputParser.ParseGuid(goalId, "goalId"),
            title,
            description,
            targetValue,
            unit,
            McpInputParser.ParseOptionalDate(deadline, "deadline"));

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Updated goal {goalId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "delete_goal"), Description("Delete a goal by ID.")]
    public async Task<string> DeleteGoal(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new DeleteGoalCommand(userId, McpInputParser.ParseGuid(goalId, "goalId"));
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Deleted goal {goalId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "update_goal_progress"), Description("Update a goal's current progress value.")]
    public async Task<string> UpdateGoalProgress(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("New current value")] decimal currentValue,
        [Description("Optional note about this progress update")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new UpdateGoalProgressCommand(
            userId,
            McpInputParser.ParseGuid(goalId, "goalId"),
            currentValue,
            note);

        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Updated progress for goal {goalId} to {currentValue}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "update_goal_status"), Description("Change a goal's status (Active, Completed, or Abandoned).")]
    public async Task<string> UpdateGoalStatus(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("New status: Active, Completed, or Abandoned")] string status,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var newStatus = Enum.Parse<GoalStatus>(status, true);
        var command = new UpdateGoalStatusCommand(userId, McpInputParser.ParseGuid(goalId, "goalId"), newStatus);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Updated goal {goalId} status to {status}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "reorder_goals"), Description("Reorder goals by setting new positions.")]
    public async Task<string> ReorderGoals(
        ClaimsPrincipal user,
        [Description("JSON array of objects with 'id' (GUID) and 'position' (int)")] string positionsJson,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var items = System.Text.Json.JsonSerializer.Deserialize<List<GoalPositionDto>>(
            positionsJson,
            CaseInsensitiveJsonOptions)
            ?? [];

        var positions = items.Select(p => new GoalPositionUpdate(McpInputParser.ParseGuid(p.Id, "id"), p.Position)).ToList();
        var command = new ReorderGoalsCommand(userId, positions);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Reordered {positions.Count} goals"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "link_habits_to_goal"), Description("Link habits to a goal. Pass the full list of habit IDs (replaces existing links).")]
    public async Task<string> LinkHabitsToGoal(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("Comma-separated habit IDs (GUIDs)")] string habitIds,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var ids = habitIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => McpInputParser.ParseGuid(s, "habitIds")).ToList();

        var command = new LinkHabitsToGoalCommand(userId, McpInputParser.ParseGuid(goalId, "goalId"), ids);
        var result = await mediator.Send(command, cancellationToken);
        return result.IsSuccess
            ? $"Linked {ids.Count} habits to goal {goalId}"
            : $"Error: {result.Error}";
    }

    [McpServerTool(Name = "get_goal_metrics"), Description("Get metrics for a goal: progress percentage, velocity, projected completion, and linked habit adherence.")]
    public async Task<string> GetGoalMetrics(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetGoalMetricsQuery(userId, McpInputParser.ParseGuid(goalId, "goalId"));
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var m = result.Value;
        var info = $"Metrics for goal {goalId}:\n" +
                   $"Progress: {m.ProgressPercentage:F1}%\n" +
                   $"Velocity: {m.VelocityPerDay:F2}/day\n" +
                   $"Tracking: {m.TrackingStatus}\n" +
                   (m.ProjectedCompletionDate is not null ? $"Projected completion: {m.ProjectedCompletionDate}\n" : "") +
                   (m.DaysToDeadline is not null ? $"Days to deadline: {m.DaysToDeadline}\n" : "");

        if (m.HabitAdherence.Count > 0)
        {
            info += "Linked habit performance:\n" +
                    string.Join("\n", m.HabitAdherence.Select(h =>
                        $"  - {h.HabitTitle}: weekly {h.WeeklyCompletionRate:F0}%, streak {h.CurrentStreak}d"));
        }

        return info;
    }

    [McpServerTool(Name = "get_goal_review"), Description("Get an AI-generated review of all active goals. Requires Pro subscription.")]
    public async Task<string> GetGoalReview(
        ClaimsPrincipal user,
        [Description("Language code (en, pt-BR)")] string language = "en",
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetGoalReviewQuery(userId, language);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var r = result.Value;
        return $"Goal Review{(r.FromCache ? " (cached)" : "")}:\n{r.Review}";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        if (!Guid.TryParse(claim, out var userId))
            throw new UnauthorizedAccessException("User ID claim is not a valid GUID");
        return userId;
    }

    private sealed record GoalPositionDto(string Id, int Position);
}
