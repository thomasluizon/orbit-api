using System.Text.Json;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class UpdateGoalStatusTool(
    IGenericRepository<Goal> goalRepository,
    IGoalCompletionService goalCompletionService,
    IUnitOfWork unitOfWork) : IAiTool, IConcurrencyRetryableTool
{
    public string Name => "update_goal_status";

    public string Description =>
        "Update a goal's status. Use this to mark a goal as completed, abandoned, or active again.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            goal_id = new { type = JsonSchemaTypes.String, description = "ID of the goal to update." },
            status = new
            {
                type = JsonSchemaTypes.String,
                description = "New goal status.",
                @enum = new[] { "Active", "Completed", "Abandoned" }
            }
        },
        required = new[] { "goal_id", "status" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("goal_id", out var goalIdEl)
            || !Guid.TryParse(goalIdEl.GetString(), out var goalId))
        {
            return new ToolResult(false, Error: "goal_id is required and must be a valid GUID.");
        }

        if (!args.TryGetProperty("status", out var statusEl)
            || statusEl.ValueKind != JsonValueKind.String
            || !Enum.TryParse<GoalStatus>(statusEl.GetString(), ignoreCase: true, out var status))
        {
            return new ToolResult(false, Error: "status is required and must be Active, Completed, or Abandoned.");
        }

        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == goalId && g.UserId == userId && !g.IsDeleted,
            cancellationToken: ct);

        if (goal is null)
            return new ToolResult(false, Error: $"Goal {goalId} not found.");

        var goalTitle = goal.Title;
        var result = status switch
        {
            GoalStatus.Completed => goal.MarkCompleted(),
            GoalStatus.Abandoned => goal.MarkAbandoned(),
            GoalStatus.Active => goal.Reactivate(),
            _ => Orbit.Domain.Common.Result.Failure(ErrorMessages.InvalidGoalStatus)
        };

        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        if (status == GoalStatus.Completed)
            await goalCompletionService.SaveCompletedGoalAsync(userId, goalId, ct);
        else
            await unitOfWork.SaveChangesAsync(ct);

        return new ToolResult(true, EntityId: goalId.ToString(), EntityName: goalTitle);
    }
}
