using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class LogHabitTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "log_habit";

    public string Description =>
        "Log a habit as completed for today. If already logged today, this will unlog it (toggle behavior). Include a note if the user shares context about the activity.";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new
        {
            habit_id = new { type = "STRING", description = "ID of the habit to log" },
            note = new { type = "STRING", description = "Optional note about the completion" }
        },
        required = new[] { "habit_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        var habit = await habitRepository.GetByIdAsync(habitId, ct);
        if (habit is null)
            return new ToolResult(false, Error: $"Habit {habitId} not found.");

        if (habit.UserId != userId)
            return new ToolResult(false, Error: "Habit does not belong to this user.");

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        string? note = null;
        if (args.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String)
            note = noteEl.GetString();

        var logResult = habit.Log(today, note);
        if (logResult.IsFailure)
            return new ToolResult(false, Error: logResult.Error);

        await habitLogRepository.AddAsync(logResult.Value, ct);
        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }
}
