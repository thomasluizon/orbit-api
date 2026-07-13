using System.Text.Json;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class BulkSkipHabitsTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "bulk_skip_habits";

    public string Description =>
        "Skip multiple habits for today in a single operation. Use this only for habits the user EXPLICITLY mentioned skipping - never include extra habits that share a tag, parent, routine, or theme but were not named. For recurring habits, advances due date to next scheduled occurrence. For one-time tasks, postpones to tomorrow. Does not log completion. Works on habits that are due today or overdue.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                items = new { type = JsonSchemaTypes.String },
                description = "Array of habit IDs to skip"
            },
            date = new
            {
                type = JsonSchemaTypes.String,
                nullable = true,
                description = "Date to skip in YYYY-MM-DD format (defaults to today)"
            }
        },
        required = new[] { "habit_ids" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var weekStartDay = await userDateService.GetUserWeekStartDayAsync(userId, ct);

        return await HabitToolHelpers.RunBulkHabitActionAsync(
            habitRepository, userDateService, args, userId,
            "No habits were skipped. They may be completed, not yet due, or not found.",
            (habit, targetDate, today) => TrySkipHabit(habit, targetDate, today, weekStartDay, ct),
            ct);
    }

    private async Task<bool> TrySkipHabit(Habit habit, DateOnly targetDate, DateOnly today, int weekStartDay, CancellationToken ct)
    {
        if (habit.IsCompleted)
            return false;

        if (habit.FrequencyUnit is null)
        {
            habit.PostponeTo(today.AddDays(1));
            return true;
        }

        if (!habit.IsFlexible && habit.DueDate > targetDate)
            return false;

        if (habit.IsFlexible)
        {
            var remaining = HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs, weekStartDay);
            if (remaining <= 0)
                return false;

            var skipResult = habit.SkipFlexible(targetDate);
            if (skipResult.IsFailure)
                return false;

            await habitLogRepository.AddAsync(skipResult.Value, ct);
        }
        else
        {
            habit.AdvanceDueDate(targetDate);
        }

        return true;
    }
}
