using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public class MoveHabitParentTool(
    IMediator mediator) : IAiTool
{
    public string Name => "move_habit_parent";

    public string Description =>
        "Re-parent a habit under a different parent habit, or promote it to top-level by passing a null parent_id.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit to move" },
            parent_id = new { type = JsonSchemaTypes.String, description = "ID of the new parent habit, or null to promote to top-level", nullable = true }
        },
        required = new[] { "habit_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        Guid? parentId = null;
        if (args.TryGetProperty("parent_id", out var parentEl) && parentEl.ValueKind == JsonValueKind.String)
        {
            if (!Guid.TryParse(parentEl.GetString(), out var parsedParentId))
                return new ToolResult(false, Error: "parent_id must be a valid GUID when provided.");
            parentId = parsedParentId;
        }

        var result = await mediator.Send(new MoveHabitParentCommand(userId, habitId, parentId), ct);

        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return new ToolResult(true, EntityId: habitId.ToString());
    }
}
