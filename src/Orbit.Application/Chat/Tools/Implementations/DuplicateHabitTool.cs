using System.Text.Json;
using MediatR;

namespace Orbit.Application.Chat.Tools.Implementations;

public class DuplicateHabitTool(
    IMediator mediator) : IAiTool
{
    public string Name => "duplicate_habit";

    public string Description =>
        "Create an exact copy of an existing habit with all its properties.";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new
        {
            habit_id = new { type = "STRING", description = "ID of the habit to duplicate" }
        },
        required = new[] { "habit_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        var result = await mediator.Send(
            new Orbit.Application.Habits.Commands.DuplicateHabitCommand(userId, habitId), ct);

        if (result.IsFailure)
            return new ToolResult(false, Error: result.Error);

        return new ToolResult(true, EntityId: result.Value.ToString(), EntityName: "Duplicated habit");
    }
}
