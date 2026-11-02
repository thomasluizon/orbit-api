using System.Text.Json;
using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class DeleteGoalTool(
    IMediator mediator) : IAiTool, IConcurrencyRetryableTool, IArgumentCheckTool
{
    public string Name => "delete_goal";

    public string Description =>
        "Delete a goal only when requested.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            goal_id = new { type = JsonSchemaTypes.String }
        },
        required = new[] { "goal_id" }
    };

    public async Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!GoalToolHelpers.TryParseGoalId(args, out var goalId))
            return Orbit.Domain.Common.Result.Failure("Invalid goal_id.");
        var result = await ChatToolArgumentCheck.CheckCommandAsync(mediator, new DeleteGoalCommand(userId, goalId), ct);
        return result.Success ? Orbit.Domain.Common.Result.Success() : Orbit.Domain.Common.Result.Failure(result.Error!);
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!GoalToolHelpers.TryParseGoalId(args, out var goalId))
            return GoalToolHelpers.InvalidGoalIdResult();

        var result = await mediator.Send(new DeleteGoalCommand(userId, goalId), ct);
        if (result.IsFailure)
            return result.ErrorCode == ErrorMessages.GoalNotFound.Code || result.Error == ErrorMessages.GoalNotFound.Message
                ? GoalToolHelpers.GoalNotFoundResult(goalId) : ToolResult.FromFailure(result);
        return new ToolResult(true, EntityId: goalId.ToString());
    }
}
