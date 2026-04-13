using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class UpdateGoalStatusTool(
    IGenericRepository<Goal> goalRepository,
    IGamificationService gamificationService,
    IUnitOfWork unitOfWork,
    ILogger<UpdateGoalStatusTool> logger) : IAiTool
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

        var result = status switch
        {
            GoalStatus.Completed => goal.MarkCompleted(),
            GoalStatus.Abandoned => goal.MarkAbandoned(),
            GoalStatus.Active => goal.Reactivate(),
            _ => Orbit.Domain.Common.Result.Failure("Invalid status.")
        };

        if (result.IsFailure)
            return new ToolResult(false, Error: result.Error);

        await unitOfWork.SaveChangesAsync(ct);

        if (status == GoalStatus.Completed)
        {
            try
            {
                await gamificationService.ProcessGoalCompleted(userId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Gamification processing failed for goal completion by user {UserId}", userId);
            }
        }

        return new ToolResult(true, EntityId: goal.Id.ToString(), EntityName: goal.Title);
    }
}
