using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class DeleteGoalTool(
    IGenericRepository<Goal> goalRepository,
    IUnitOfWork unitOfWork) : IAiTool
{
    public string Name => "delete_goal";

    public string Description =>
        "Delete an existing goal. Use this only when the user clearly wants a goal removed.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            goal_id = new { type = JsonSchemaTypes.String, description = "ID of the goal to delete." }
        },
        required = new[] { "goal_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("goal_id", out var goalIdEl)
            || !Guid.TryParse(goalIdEl.GetString(), out var goalId))
        {
            return new ToolResult(false, Error: "goal_id is required and must be a valid GUID.");
        }

        var goal = await goalRepository.FindOneTrackedAsync(
            g => g.Id == goalId && g.UserId == userId && !g.IsDeleted,
            cancellationToken: ct);

        if (goal is null)
            return new ToolResult(false, Error: $"Goal {goalId} not found.");

        goal.SoftDelete();
        await unitOfWork.SaveChangesAsync(ct);

        return new ToolResult(true, EntityId: goal.Id.ToString(), EntityName: goal.Title);
    }
}
