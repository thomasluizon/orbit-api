using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class UpdateGoalProgressTool(
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<GoalProgressLog> progressLogRepository,
    IUnitOfWork unitOfWork) : IAiTool
{
    public string Name => "update_goal_progress";
    public string Description => "Update progress on an existing goal. Identify the goal by goal_id, or by fuzzy goal_name match, then set the new current value.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            goal_id = new { type = JsonSchemaTypes.String, description = "ID of the goal to update. Provide either goal_id OR goal_name." },
            goal_name = new { type = JsonSchemaTypes.String, description = "Name or partial name of the goal to update. Provide either goal_id OR goal_name." },
            current_value = new { type = "number", description = "New current progress value" },
            note = new { type = JsonSchemaTypes.String, description = "Optional note about this progress update" }
        },
        required = new[] { "current_value" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("current_value", out var valueEl) || valueEl.ValueKind != JsonValueKind.Number)
            return new ToolResult(false, Error: "current_value is required and must be a number.");

        string? note = args.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String ? noteEl.GetString() : null;

        var (goal, error) = await ResolveGoalAsync(args, userId, ct);
        if (goal is null) return new ToolResult(false, Error: error);

        var previousValue = goal.CurrentValue;
        var progressLog = GoalProgressLog.Create(goal.Id, previousValue, valueEl.GetDecimal(), note);
        await progressLogRepository.AddAsync(progressLog, ct);

        var result = goal.UpdateProgress(valueEl.GetDecimal());
        if (result.IsFailure) return ToolResult.FromFailure(result);

        await unitOfWork.SaveChangesAsync(ct);
        return new ToolResult(true, EntityId: goal.Id.ToString(), EntityName: goal.Title);
    }

    private async Task<(Goal? Goal, string? Error)> ResolveGoalAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (args.TryGetProperty("goal_id", out var idEl) && Guid.TryParse(idEl.GetString(), out var goalId))
        {
            var byId = await goalRepository.FindOneTrackedAsync(
                g => g.Id == goalId && g.UserId == userId, cancellationToken: ct);
            return byId is null ? (null, $"Goal {goalId} not found.") : (byId, null);
        }

        if (!args.TryGetProperty("goal_name", out var nameEl) || string.IsNullOrWhiteSpace(nameEl.GetString()))
            return (null, "Provide either goal_id or goal_name.");

        var goalName = nameEl.GetString() ?? string.Empty;
        var goals = await goalRepository.FindTrackedAsync(g => g.UserId == userId && g.Status == Domain.Enums.GoalStatus.Active, ct);
        var goal = goals.FirstOrDefault(g => g.Title.Equals(goalName, StringComparison.OrdinalIgnoreCase))
            ?? goals.FirstOrDefault(g => g.Title.Contains(goalName, StringComparison.OrdinalIgnoreCase));

        return goal is null ? (null, $"No active goal found matching '{goalName}'.") : (goal, null);
    }
}
