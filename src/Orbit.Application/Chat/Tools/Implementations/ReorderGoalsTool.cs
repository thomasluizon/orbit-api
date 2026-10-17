using System.Text.Json;
using MediatR;
using Orbit.Application.Goals.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class ReorderGoalsTool(
    IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "reorder_goals";

    public string Description =>
        "Set new display positions for goals. Pass each goal ID with its target zero-based position.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            positions = new
            {
                type = JsonSchemaTypes.Array,
                description = "Goal positions to apply.",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        goal_id = new { type = JsonSchemaTypes.String, description = "ID of the goal to position" },
                        position = new { type = JsonSchemaTypes.Integer, description = "Zero-based target position" }
                    },
                    required = new[] { "goal_id", "position" }
                }
            }
        },
        required = new[] { "positions" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        if (!args.TryGetProperty("positions", out var positionsEl) || positionsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "positions is required and must be an array.");

        var positions = new List<GoalPositionUpdate>();
        foreach (var item in positionsEl.EnumerateArray())
        {
            var goalIdValue = JsonArgumentParser.GetOptionalString(item, "goal_id");
            var position = JsonArgumentParser.GetOptionalInt(item, "position");
            if (goalIdValue is null || position is null || !Guid.TryParse(goalIdValue, out var goalId))
                return new ToolResult(false, Error: "Each position requires a valid goal_id GUID and an integer position.");

            positions.Add(new GoalPositionUpdate(goalId, position.Value));
        }

        var command = new ReorderGoalsCommand(userId, positions);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);

        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return new ToolResult(true, EntityName: $"{positions.Count} goals");
    }
}
