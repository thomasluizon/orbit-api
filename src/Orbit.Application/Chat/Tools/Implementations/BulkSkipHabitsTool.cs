using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
        if (!args.TryGetProperty("habit_ids", out var idsEl) || idsEl.ValueKind != JsonValueKind.Array)
            return new ToolResult(false, Error: "habit_ids is required and must be an array of GUIDs.");

        var habitIds = new List<Guid>();
        foreach (var el in idsEl.EnumerateArray())
        {
            if (Guid.TryParse(el.GetString(), out var id))
                habitIds.Add(id);
        }

        if (habitIds.Count == 0)
            return new ToolResult(false, Error: "No valid habit IDs provided.");

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var targetDate = JsonArgumentParser.ParseDateOnly(args, "date") ?? today;
        var skippedNames = new List<string>();

        // Batch-load all requested habits in a single query instead of N+1
        var habits = await habitRepository.FindTrackedAsync(
            h => habitIds.Contains(h.Id) && h.UserId == userId,
            q => q.Include(h => h.Logs),
            ct);

        foreach (var habitId in habitIds)
        {
            var habit = habits.FirstOrDefault(h => h.Id == habitId);
            if (habit is not null && await TrySkipHabit(habit, targetDate, today, ct))
                skippedNames.Add(habit.Title);
        }

        if (skippedNames.Count == 0)
            return new ToolResult(false, Error: "No habits were skipped. They may be completed, not yet due, or not found.");

        return new ToolResult(true, EntityName: string.Join(", ", skippedNames));
    }

    private async Task<bool> TrySkipHabit(Habit habit, DateOnly targetDate, DateOnly today, CancellationToken ct)
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
            var remaining = HabitScheduleService.GetRemainingCompletions(habit, targetDate, habit.Logs);
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
