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
    public string Description => "Update progress on an existing goal. Finds the goal by fuzzy title match and sets the new current value.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            goal_name = new { type = JsonSchemaTypes.String, description = "Name or partial name of the goal to update" },
            current_value = new { type = "number", description = "New current progress value" },
            note = new { type = JsonSchemaTypes.String, description = "Optional note about this progress update" }
        },
        required = new[] { "goal_name", "current_value" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("goal_name", out var nameEl) || string.IsNullOrWhiteSpace(nameEl.GetString()))
            return new ToolResult(false, Error: "goal_name is required.");
        if (!args.TryGetProperty("current_value", out var valueEl) || valueEl.ValueKind != JsonValueKind.Number)
            return new ToolResult(false, Error: "current_value is required and must be a number.");

        var goalName = nameEl.GetString() ?? string.Empty;
        string? note = args.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String ? noteEl.GetString() : null;

        var goals = await goalRepository.FindTrackedAsync(g => g.UserId == userId && g.Status == Domain.Enums.GoalStatus.Active, ct);
        var goal = goals.FirstOrDefault(g => g.Title.Equals(goalName, StringComparison.OrdinalIgnoreCase))
            ?? goals.FirstOrDefault(g => g.Title.Contains(goalName, StringComparison.OrdinalIgnoreCase));

        if (goal is null) return new ToolResult(false, Error: "No active goal found matching '" + goalName + "'.");

        var previousValue = goal.CurrentValue;
        var progressLog = GoalProgressLog.Create(goal.Id, previousValue, valueEl.GetDecimal(), note);
        await progressLogRepository.AddAsync(progressLog, ct);

        var result = goal.UpdateProgress(valueEl.GetDecimal());
        if (result.IsFailure) return new ToolResult(false, Error: result.Error);

        await unitOfWork.SaveChangesAsync(ct);
        return new ToolResult(true, EntityId: goal.Id.ToString(), EntityName: goal.Title);
    }
}
