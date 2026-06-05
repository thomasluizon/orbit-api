using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Enums;

namespace Orbit.Api.Mcp.Tools;

/// <summary>
/// MCP goal tools. Mutations route through <see cref="McpExecutorBridge"/> →
/// <see cref="Orbit.Domain.Interfaces.IAgentOperationExecutor"/> with
/// <see cref="Orbit.Domain.Models.AgentExecutionSurface.Mcp"/>, sharing the policy evaluation
/// (read-only-credential denial, ownership pre-check, Pro/feature-flag gating) and the
/// <c>AgentAuditLogs</c> trail used by every other agent surface; each forwards a snake_case
/// argument object matching its backing <c>IAiTool</c> schema and formats the result into the
/// legacy string contract. Read/query tools stay on MediatR.
/// </summary>
[McpServerToolType]
public class GoalTools(IMediator mediator, McpExecutorBridge executorBridge)
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
        var result = await executorBridge.ExecuteAsync(user, "create_goal", new
        {
            title,
            target_value = targetValue,
            unit,
            description,
            deadline,
            goal_type = type
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded
            ? $"Created goal '{title}' (id: {result.TargetId})"
            : result.Message;
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
        var result = await executorBridge.ExecuteAsync(user, "update_goal", new
        {
            goal_id = goalId,
            title,
            target_value = targetValue,
            unit,
            description,
            deadline
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Updated goal {goalId}" : result.Message;
    }

    [McpServerTool(Name = "delete_goal"), Description("Delete a goal by ID.")]
    public async Task<string> DeleteGoal(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("Confirmation token returned by confirm_agent_operation_v2 (required: deleting a goal is destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "delete_goal", new
        {
            goal_id = goalId
        }, confirmationToken, cancellationToken);

        return result.Succeeded ? $"Deleted goal {goalId}" : result.Message;
    }

    [McpServerTool(Name = "update_goal_progress"), Description("Update a goal's current progress value.")]
    public async Task<string> UpdateGoalProgress(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("New current value")] decimal currentValue,
        [Description("Optional note about this progress update")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "update_goal_progress", new
        {
            goal_id = goalId,
            current_value = currentValue,
            note
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded
            ? $"Updated progress for goal {goalId} to {currentValue}"
            : result.Message;
    }

    [McpServerTool(Name = "update_goal_status"), Description("Change a goal's status (Active, Completed, or Abandoned).")]
    public async Task<string> UpdateGoalStatus(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("New status: Active, Completed, or Abandoned")] string status,
        CancellationToken cancellationToken = default)
    {
        var result = await executorBridge.ExecuteAsync(user, "update_goal_status", new
        {
            goal_id = goalId,
            status
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Updated goal {goalId} status to {status}" : result.Message;
    }

    [McpServerTool(Name = "reorder_goals"), Description("Reorder goals by setting new positions.")]
    public async Task<string> ReorderGoals(
        ClaimsPrincipal user,
        [Description("JSON array of objects with 'id' (GUID) and 'position' (int)")] string positionsJson,
        CancellationToken cancellationToken = default)
    {
        var items = System.Text.Json.JsonSerializer.Deserialize<List<GoalPositionDto>>(
            positionsJson,
            CaseInsensitiveJsonOptions)
            ?? [];

        var result = await executorBridge.ExecuteAsync(user, "reorder_goals", new
        {
            positions = items.Select(p => new { goal_id = p.Id, position = p.Position })
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Reordered {items.Count} goals" : result.Message;
    }

    [McpServerTool(Name = "link_habits_to_goal"), Description("Link habits to a goal. Pass the full list of habit IDs (replaces existing links).")]
    public async Task<string> LinkHabitsToGoal(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("Comma-separated habit IDs (GUIDs)")] string habitIds,
        CancellationToken cancellationToken = default)
    {
        var ids = habitIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => McpInputParser.ParseGuid(s, "habitIds")).ToList();

        var result = await executorBridge.ExecuteAsync(user, "link_habits_to_goal", new
        {
            goal_id = goalId,
            habit_ids = ids.Select(i => i.ToString())
        }, confirmationToken: null, cancellationToken);

        return result.Succeeded ? $"Linked {ids.Count} habits to goal {goalId}" : result.Message;
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
