using System.Text.Json;
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
        "Log multiple habits as completed for today in a single operation. Use this only for habits the user EXPLICITLY mentioned completing - never include extra habits that share a tag, parent, routine, or theme but were not named.";

    public object GetParameterSchema() => HabitToolHelpers.BulkHabitActionSchema(
        "Array of habit IDs to log as completed",
        "Date to log for in YYYY-MM-DD format (defaults to today)");

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        HabitToolHelpers.RunBulkHabitActionAsync(
            habitRepository, userDateService, args, userId,
            "No habits were logged. They may already be completed or not found.",
            (habit, targetDate, _) => TryLogHabit(habit, targetDate, ct),
            ct);

    private async Task<bool> TryLogHabit(Habit habit, DateOnly targetDate, CancellationToken ct)
    {
        if (habit.Logs.Any(l => l.Date == targetDate))
            return false;

        var logResult = habit.Log(targetDate);
        if (logResult.IsFailure)
            return false;

        await habitLogRepository.AddAsync(logResult.Value, ct);
        return true;
    }
}
