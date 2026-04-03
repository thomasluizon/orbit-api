using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class SkipHabitTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "skip_habit";

    public string Description =>
        "Skip a habit for a specific date (defaults to today). For recurring habits, advances the due date to the next scheduled occurrence. For one-time tasks, postpones to tomorrow. Does not log completion. Works on habits that are DUE or OVERDUE. Use when the user says 'skip', 'pass on', 'not today', 'postpone', or 'dismiss' for a habit.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit to skip" },
            date = new { type = JsonSchemaTypes.String, description = "ISO date (YYYY-MM-DD) to skip a specific instance. Defaults to today." }
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
            q => q.Include(h => h.Logs),
            ct);

        if (habit is null)
            return new ToolResult(false, Error: $"Habit {habitId} not found.");

        if (habit.IsCompleted)
            return new ToolResult(false, Error: "Cannot skip a completed habit.");

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        if (habit.FrequencyUnit is null)
        {
            // One-time task: postpone to tomorrow
            habit.PostponeTo(today.AddDays(1));
            return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
        }

        DateOnly targetDate = today;
        if (args.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
        {
            if (DateOnly.TryParseExact(dateEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
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
        {
            var remaining = HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs);
            if (remaining <= 0)
                return new ToolResult(false, Error: "All instances for this period have already been completed or skipped.");

            var skipResult = habit.SkipFlexible(targetDate);
            if (skipResult.IsFailure)
                return new ToolResult(false, Error: skipResult.Error);

            await habitLogRepository.AddAsync(skipResult.Value, ct);
        }
        else
        {
            habit.AdvanceDueDate(targetDate);
        }

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }
}
