using System.Text.Json;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SkipHabitTool(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "skip_habit";

    public string Description =>
        "Skip a recurring habit for a specific date (defaults to today) - advances the due date to the next scheduled occurrence without logging it as completed. Only works on recurring habits (not one-time tasks) that are DUE or OVERDUE. Use when the user says 'skip', 'pass on', 'not today', or 'dismiss' for a habit. Use the date parameter to skip overdue instances.";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new
        {
            habit_id = new { type = "STRING", description = "ID of the habit to skip" },
            date = new { type = "STRING", description = "ISO date (YYYY-MM-DD) to skip a specific instance. Defaults to today." }
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

        DateOnly targetDate = today;
        if (args.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
        {
            if (DateOnly.TryParse(dateEl.GetString(), out var parsed))
                targetDate = parsed;
            else
                return new ToolResult(false, Error: "Invalid date format. Use YYYY-MM-DD.");
        }

        if (targetDate > today)
            return new ToolResult(false, Error: "Cannot skip a future date.");

        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return new ToolResult(false, Error: "Cannot skip a habit that is not yet due.");

        if (!habit.IsFlexible && !HabitScheduleService.IsHabitDueOnDate(habit, targetDate))
            return new ToolResult(false, Error: "Habit is not scheduled on this date.");

        if (habit.IsFlexible)
            habit.AdvanceDueDatePastWindow(today);
        else
            habit.AdvanceDueDate(targetDate);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }
}
