using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class DeleteHabitTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository) : IAiTool
{
    public string Name => "delete_habit";

    public string Description =>
        "Delete a habit and its sub-habits. For single deletions, execute immediately. For bulk deletions (2+ habits), list them in your message and ask for confirmation first - only delete after they confirm.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit to delete" }
        },
        required = new[] { "habit_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();

        var habit = await HabitToolHelpers.FindHabitAsync(habitRepository, habitId, userId, ct);
        if (habit is null)
            return HabitToolHelpers.HabitNotFoundResult(habitId);

        var title = habit.Title;
        var result = await mediator.Send(new DeleteHabitCommand(userId, habitId), ct);
        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return new ToolResult(true, EntityId: habitId.ToString(), EntityName: title);
    }
}
