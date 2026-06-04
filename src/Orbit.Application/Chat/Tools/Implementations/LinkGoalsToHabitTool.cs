using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class LinkGoalsToHabitTool(
    IMediator mediator) : IAiTool
{
    public string Name => "link_goals_to_habit";

    public string Description =>
        "Link one or more goals to a habit. Replaces all existing goal links for the habit. Pass the full desired list of goal IDs.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit to link goals to" },
            goal_ids = new
            {
                type = JsonSchemaTypes.Array,
                description = "IDs of goals to link to the habit. Replaces all existing links.",
                items = new { type = JsonSchemaTypes.String }
            }
        },
        required = new[] { "habit_id", "goal_ids" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        if (!args.TryGetProperty("goal_ids", out var goalIdsEl) || goalIdsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "goal_ids is required and must be an array.");

        var goalIds = JsonArgumentParser.ParseGuidArray(args, "goal_ids") ?? new List<Guid>();

        var result = await mediator.Send(new LinkGoalsToHabitCommand(userId, habitId, goalIds), ct);

        if (result.IsFailure)
            return new ToolResult(false, Error: result.Error);

        return new ToolResult(true, EntityId: habitId.ToString());
    }
}
