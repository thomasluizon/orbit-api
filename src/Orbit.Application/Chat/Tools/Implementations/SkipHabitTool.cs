using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SkipHabitTool(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "skip_habit";

    public string Description =>
        "Skip a recurring habit for today - advances the due date to the next scheduled occurrence without logging it as completed. Only works on recurring habits (not one-time tasks) that are DUE TODAY or OVERDUE. Use when the user says 'skip', 'pass on', 'not today', or 'dismiss' for a habit.";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new
        {
            habit_id = new { type = "STRING", description = "ID of the habit to skip" }
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

        if (habit.IsCompleted)
            return new ToolResult(false, Error: "Cannot skip a completed habit.");

        if (habit.FrequencyUnit is null)
            return new ToolResult(false, Error: "Cannot skip a one-time task.");

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        if (habit.DueDate > today)
            return new ToolResult(false, Error: "Cannot skip a habit that is not yet due.");

        habit.AdvanceDueDate(today);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }
}
