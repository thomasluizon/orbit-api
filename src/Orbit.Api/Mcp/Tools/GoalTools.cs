using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Enums;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class GoalTools(IMediator mediator, McpExecutorBridge executorBridge)
{
    [McpServerTool(Name = "list_goals"), Description("List all goals for the authenticated user.")]
    public async Task<CallToolResult> ListGoals(
        ClaimsPrincipal user,
        [Description("Filter by status: Active, Completed, or Abandoned")] string? status = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            GoalStatus? statusFilter = null;
            if (status is not null)
            {
                if (!Enum.TryParse<GoalStatus>(status, true, out var parsedStatus) || !Enum.IsDefined(parsedStatus))
                    return McpToolHelpers.Error($"Invalid status: '{status}' is not a defined goal status.");
                statusFilter = parsedStatus;
            }
            var result = await mediator.Send(new GetGoalsQuery(userId, statusFilter), cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var goals = result.Value;
            if (goals.Items.Count == 0)
                return McpToolHelpers.Text("No goals found.");
            var lines = goals.Items.Select(g => string.Create(CultureInfo.InvariantCulture, $"- {g.Title} (id: {g.Id}) | {g.CurrentValue}/{g.TargetValue} {g.Unit} ({g.ProgressPercentage:F0}%)") + $" | Status: {g.Status}" + (g.Deadline is not null ? string.Create(CultureInfo.InvariantCulture, $" | Deadline: {g.Deadline}") : "") + (g.TrackingStatus is not null ? $" | Tracking: {g.TrackingStatus}" : "") + $" | Linked habits: {(g.LinkedHabits.Count == 0 ? "none" : string.Join(", ", g.LinkedHabits.Select(h => $"{h.Title} ({h.Id})")))}");
            return McpToolHelpers.Text($"Goals ({goals.TotalCount}):\n{string.Join("\n", lines)}");
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

    [McpServerTool(Name = "create_goal"), Description("Create a new goal. Requires Pro subscription.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "CreateGoal preserves flat title, targetValue, unit, description, deadline and type schema properties. MCP schema generation requires these separate parameters.")]
    public async Task<CallToolResult> CreateGoal(
        ClaimsPrincipal user,
        [Description("Goal title")] string title,
        [Description("Target value to reach")] decimal targetValue,
        [Description("Unit of measurement (e.g., km, books, hours)")] string unit,
        [Description("Optional description")] string? description = null,
        [Description("Optional deadline in YYYY-MM-DD format")] string? deadline = null,
        [Description("Goal type: Standard (default) or Streak")] string type = "Standard",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await executorBridge.ExecuteAsync(user, "create_goal", new { title, target_value = targetValue, unit, description, deadline, goal_type = type }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Created goal '{title}' (id: {result.TargetId})") : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "get_goal"), Description("Get detailed information about a specific goal by ID, including progress history and linked habits.")]
    public async Task<CallToolResult> GetGoal(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var query = new GetGoalByIdQuery(userId, McpInputParser.ParseGuid(goalId, "goalId"));
            var result = await mediator.Send(query, cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var g = result.Value;
            var goalSummary = $"Title: {g.Title}\nID: {g.Id}\n" + string.Create(CultureInfo.InvariantCulture, $"Progress: {g.CurrentValue}/{g.TargetValue} {g.Unit} ({g.ProgressPercentage:F1}%)\n") + $"Status: {g.Status}\n" + (g.Description is not null ? $"Description: {g.Description}\n" : "") + (g.Deadline is not null ? string.Create(CultureInfo.InvariantCulture, $"Deadline: {g.Deadline}\n") : "") + string.Create(CultureInfo.InvariantCulture, $"Created: {g.CreatedAtUtc:yyyy-MM-dd}\n") + (g.CompletedAtUtc is not null ? string.Create(CultureInfo.InvariantCulture, $"Completed: {g.CompletedAtUtc:yyyy-MM-dd}\n") : "") + (g.LinkedHabits.Count > 0 ? $"Linked habits: {string.Join(", ", g.LinkedHabits.Select(h => $"{h.Title} ({h.Id})"))}\n" : "") + (g.ProgressHistory.Count > 0 ? $"Recent progress: {string.Join(", ", g.ProgressHistory.Take(5).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.PreviousValue}->{p.Value}")))}\n" : "");
            return McpToolHelpers.Text(goalSummary);
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

    [McpServerTool(Name = "update_goal"), Description("Update a goal's title, description, target value, unit, or deadline.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "UpdateGoal preserves flat goalId, title, targetValue, unit, description and deadline schema properties. MCP schema generation requires these separate parameters.")]
    public async Task<CallToolResult> UpdateGoal(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("Goal title")] string title,
        [Description("Target value")] decimal targetValue,
        [Description("Unit of measurement")] string unit,
        [Description("Optional description")] string? description = null,
        [Description("Optional deadline in YYYY-MM-DD format")] string? deadline = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(goalId, "goalId");
            var result = await executorBridge.ExecuteAsync(user, "update_goal", new { goal_id = goalId, title, target_value = targetValue, unit, description, deadline }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Updated goal {goalId}") : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "delete_goal"), Description("Delete a goal by ID.")]
    public async Task<CallToolResult> DeleteGoal(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("Confirmation token returned by confirm_agent_operation_v2 through the Claude connector; manual API keys must use the Claude connector or make this change in the Orbit app. (required: deleting a goal is destructive)")] string? confirmationToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(goalId, "goalId");
            var result = await executorBridge.ExecuteAsync(user, "delete_goal", new { goal_id = goalId }, confirmationToken, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Deleted goal {goalId}") : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "update_goal_progress"), Description("Update a goal's current progress value.")]
    public async Task<CallToolResult> UpdateGoalProgress(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("New current value")] decimal currentValue,
        [Description("Optional note about this progress update")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(goalId, "goalId");
            var result = await executorBridge.ExecuteAsync(user, "update_goal_progress", new { goal_id = goalId, current_value = currentValue, note }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text(string.Create(CultureInfo.InvariantCulture, $"Updated progress for goal {goalId} to {currentValue}")) : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "update_goal_status"), Description("Change a goal's status (Active, Completed, or Abandoned).")]
    public async Task<CallToolResult> UpdateGoalStatus(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("New status: Active, Completed, or Abandoned")] string status,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(goalId, "goalId");
            var result = await executorBridge.ExecuteAsync(user, "update_goal_status", new { goal_id = goalId, status }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Updated goal {goalId} status to {status}") : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "reorder_goals"), Description("Reorder goals by setting new positions.")]
    public async Task<CallToolResult> ReorderGoals(
        ClaimsPrincipal user,
        [Description("JSON array of objects with 'id' (GUID) and 'position' (int)")] string positionsJson,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var items = McpToolHelpers.DeserializeJson<List<McpToolHelpers.GoalPositionDto>>(positionsJson);
            foreach (var item in items)
                _ = McpInputParser.ParseGuid(item.Id, "id");
            var result = await executorBridge.ExecuteAsync(user, "reorder_goals", new { positions = items.Select(p => new { goal_id = p.Id, position = p.Position }) }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Reordered {items.Count} goals") : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "link_habits_to_goal"), Description("Link habits to a goal. Pass the full list of habit IDs (replaces existing links).")]
    public async Task<CallToolResult> LinkHabitsToGoal(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        [Description("Comma-separated habit IDs (GUIDs)")] string habitIds,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = McpInputParser.ParseGuid(goalId, "goalId");
            var ids = habitIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => McpInputParser.ParseGuid(s, "habitIds")).ToList();
            var result = await executorBridge.ExecuteAsync(user, "link_habits_to_goal", new { goal_id = goalId, habit_ids = ids.Select(i => i.ToString()) }, confirmationToken: null, cancellationToken);
            return result.Succeeded ? McpToolHelpers.Text($"Linked {ids.Count} habits to goal {goalId}") : McpToolHelpers.Error(result.Message);
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

    [McpServerTool(Name = "get_goal_metrics"), Description("Get metrics for a goal: progress percentage, velocity, projected completion, and linked habit adherence.")]
    public async Task<CallToolResult> GetGoalMetrics(
        ClaimsPrincipal user,
        [Description("The goal ID (GUID)")] string goalId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var query = new GetGoalMetricsQuery(userId, McpInputParser.ParseGuid(goalId, "goalId"));
            var result = await mediator.Send(query, cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var m = result.Value;
            var metricsSummary = $"Metrics for goal {goalId}:\n" + string.Create(CultureInfo.InvariantCulture, $"Progress: {m.ProgressPercentage:F1}%\n") + string.Create(CultureInfo.InvariantCulture, $"Velocity: {m.VelocityPerDay:F2}/day\n") + $"Tracking: {m.TrackingStatus}\n" + (m.ProjectedCompletionDate is not null ? string.Create(CultureInfo.InvariantCulture, $"Projected completion: {m.ProjectedCompletionDate}\n") : "") + (m.DaysToDeadline is not null ? $"Days to deadline: {m.DaysToDeadline}\n" : "");
            if (m.HabitAdherence.Count > 0)
            {
                metricsSummary += "Linked habit performance:\n" + string.Join("\n", m.HabitAdherence.Select(h => string.Create(CultureInfo.InvariantCulture, $"  - {h.HabitTitle}: weekly {h.WeeklyCompletionRate:F0}%, streak {h.CurrentStreak}d")));
            }

            return McpToolHelpers.Text(metricsSummary);
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

    [McpServerTool(Name = "get_goal_review"), Description("Get an AI-generated review of all active goals.")]
    public async Task<CallToolResult> GetGoalReview(
        ClaimsPrincipal user,
        [Description("Language code (en, pt-BR)")] string language = "en",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var query = new GetGoalReviewQuery(userId, language);
            var result = await mediator.Send(query, cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var r = result.Value;
            return McpToolHelpers.Text($"Goal Review{(r.FromCache ? " (cached)" : "")}:\n{r.Review}");
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
