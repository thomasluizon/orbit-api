using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class BulkSkipHabitsTool(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "bulk_skip_habits";

    public string Description =>
        "Skip multiple recurring habits for today in a single operation. Advances each habit's due date to the next scheduled occurrence without logging completion. Only works on recurring habits that are due today or overdue.";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new
        {
            habit_ids = new
            {
                type = "ARRAY",
                items = new { type = "STRING" },
                description = "Array of habit IDs to skip"
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
        var skippedCount = 0;
        var skippedNames = new List<string>();

        foreach (var habitId in habitIds)
        {
            var habit = await habitRepository.FindOneTrackedAsync(
                h => h.Id == habitId && h.UserId == userId,
                cancellationToken: ct);

            if (habit is null)
                continue;

            if (habit.IsCompleted)
                continue;

            if (habit.FrequencyUnit is null)
                continue;

            if (habit.DueDate > today)
                continue;

            habit.AdvanceDueDate(today);

            skippedCount++;
            skippedNames.Add(habit.Title);
        }

        if (skippedCount == 0)
            return new ToolResult(false, Error: "No habits were skipped. They may be completed, one-time tasks, not yet due, or not found.");

        return new ToolResult(true, EntityName: string.Join(", ", skippedNames));
    }
}
