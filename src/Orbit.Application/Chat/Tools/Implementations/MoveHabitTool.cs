using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class MoveHabitTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository) : IAiTool
{
    public string Name => "move_habit";

    public string Description =>
        "Move a habit under a different parent, or make it a top-level habit by passing null as new_parent_id.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit to move" },
            new_parent_id = new { type = JsonSchemaTypes.String, description = "ID of the new parent habit, or null to make top-level", nullable = true }
        },
        required = new[] { "habit_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();

        Guid? newParentId = null;
        if (args.TryGetProperty("new_parent_id", out var parentEl)
            && parentEl.ValueKind == JsonValueKind.String
            && Guid.TryParse(parentEl.GetString(), out var parsedParentId))
            newParentId = parsedParentId;

        var result = await mediator.Send(
            new MoveHabitParentCommand(userId, habitId, newParentId), ct);
        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        var habits = await habitRepository.FindAsync(
            habit => habit.Id == habitId && habit.UserId == userId, ct);
        return new ToolResult(
            true,
            EntityId: habitId.ToString(),
            EntityName: habits.FirstOrDefault()?.Title);
    }
}
