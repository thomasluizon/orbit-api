using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class BulkLogHabitsTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "bulk_log_habits";

    public string Description =>
        "Log multiple habits as completed for today in a single operation. Use this when the user mentions completing several activities at once.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                items = new { type = JsonSchemaTypes.String },
                description = "Array of habit IDs to log as completed"
            },
            note = new { type = JsonSchemaTypes.String, description = "Optional note about the completions" }
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

        string? note = null;
        if (args.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String)
            note = noteEl.GetString();

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var loggedNames = new List<string>();

        // Batch-load all requested habits in a single query instead of N+1
        var habits = await habitRepository.FindTrackedAsync(
            h => habitIds.Contains(h.Id) && h.UserId == userId,
            q => q.Include(h => h.Logs),
            ct);

        foreach (var habitId in habitIds)
        {
            var habit = habits.FirstOrDefault(h => h.Id == habitId);
            if (habit is not null && await TryLogHabit(habit, today, note, ct))
                loggedNames.Add(habit.Title);
        }

        if (loggedNames.Count == 0)
            return new ToolResult(false, Error: "No habits were logged. They may already be completed or not found.");

        return new ToolResult(true, EntityName: string.Join(", ", loggedNames));
    }

    private async Task<bool> TryLogHabit(Habit habit, DateOnly today, string? note, CancellationToken ct)
    {
        if (habit.Logs.Any(l => l.Date == today))
            return false;

        var logResult = habit.Log(today, note);
        if (logResult.IsFailure)
            return false;

        await habitLogRepository.AddAsync(logResult.Value, ct);
        return true;
    }
}
