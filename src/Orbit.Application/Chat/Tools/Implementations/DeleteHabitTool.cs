using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class DeleteHabitTool(
    IGenericRepository<Habit> habitRepository) : IAiTool
{
    public string Name => "delete_habit";

    public string Description =>
        "Delete a habit permanently. For single deletions, execute immediately. For bulk deletions (2+ habits), list them in your message and ask for confirmation first - only delete after they confirm.";

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
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == habitId && h.UserId == userId,
            cancellationToken: ct);

        if (habit is null)
            return new ToolResult(false, Error: $"Habit {habitId} not found.");

        var title = habit.Title;
        habitRepository.Remove(habit);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: title);
    }
}
