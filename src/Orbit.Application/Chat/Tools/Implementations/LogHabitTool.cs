using System.Globalization;
using System.Text.Json;
using Orbit.Application.Common;
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
        "Log a habit as completed for a specific date (defaults to today). If already logged for that date, this will unlog it (toggle behavior). Include a note if the user shares context about the activity. Use the date parameter to log overdue instances.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit to log" },
            note = new { type = JsonSchemaTypes.String, description = "Optional note about the completion" },
            date = new { type = JsonSchemaTypes.String, description = "ISO date (YYYY-MM-DD) to log for a specific date, e.g. an overdue instance. Defaults to today." }
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
            return new ToolResult(false, Error: ErrorMessages.HabitNotOwned);

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        DateOnly targetDate = today;
        if (args.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
        {
            if (DateOnly.TryParseExact(dateEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                targetDate = parsed;
            else
                return new ToolResult(false, Error: "Invalid date format. Use YYYY-MM-DD.");
        }

        if (targetDate > today)
            return new ToolResult(false, Error: "Cannot log a future date.");

        string? note = null;
        if (args.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String)
            note = noteEl.GetString();

        var shouldAdvanceDueDate = targetDate >= today;
        var logResult = habit.Log(targetDate, note, advanceDueDate: shouldAdvanceDueDate);
        if (logResult.IsFailure)
            return new ToolResult(false, Error: logResult.Error);

        await habitLogRepository.AddAsync(logResult.Value, ct);
        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }
}
