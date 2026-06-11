using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class ReorderHabitsTool(
    IMediator mediator) : IAiTool
{
    public string Name => "reorder_habits";

    public string Description =>
        "Set new display positions for habits. Pass each habit ID with its target zero-based position.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            positions = new
            {
                type = JsonSchemaTypes.Array,
                description = "Habit positions to apply.",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit to position" },
                        position = new { type = JsonSchemaTypes.Integer, description = "Zero-based target position" }
                    },
                    required = new[] { "habit_id", "position" }
                }
            }
        },
        required = new[] { "positions" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("positions", out var positionsEl) || positionsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "positions is required and must be an array.");

        var positions = new List<HabitPositionUpdate>();
        foreach (var item in positionsEl.EnumerateArray())
        {
            var habitIdValue = JsonArgumentParser.GetOptionalString(item, "habit_id");
            var position = JsonArgumentParser.GetOptionalInt(item, "position");
            if (habitIdValue is null || position is null || !Guid.TryParse(habitIdValue, out var habitId))
                return new ToolResult(false, Error: "Each position requires a valid habit_id GUID and an integer position.");

            positions.Add(new HabitPositionUpdate(habitId, position.Value));
        }

        var result = await mediator.Send(new ReorderHabitsCommand(userId, positions), ct);

        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return new ToolResult(true, EntityName: $"{positions.Count} habits");
    }
}
