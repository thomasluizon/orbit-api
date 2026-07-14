using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class DeleteGoalTool(
    IGenericRepository<Goal> goalRepository,
    IUnitOfWork unitOfWork) : IAiTool, IConcurrencyRetryableTool
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
        if (!GoalToolHelpers.TryParseGoalId(args, out var goalId))
            return GoalToolHelpers.InvalidGoalIdResult();

        var goal = await GoalToolHelpers.FindGoalAsync(goalRepository, goalId, userId, ct);
        if (goal is null)
            return GoalToolHelpers.GoalNotFoundResult(goalId);

        goal.SoftDelete();
        await unitOfWork.SaveChangesAsync(ct);

        return new ToolResult(true, EntityId: goal.Id.ToString(), EntityName: goal.Title);
    }
}
