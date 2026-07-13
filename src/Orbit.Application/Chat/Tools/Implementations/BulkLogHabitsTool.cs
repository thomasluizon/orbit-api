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
            date = new
            {
                type = JsonSchemaTypes.String,
                nullable = true,
                description = "Date to log for in YYYY-MM-DD format (defaults to today)"
            }
        },
        required = new[] { "habit_ids" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (habitIds, parseError) = HabitToolHelpers.ParseHabitIds(args);
        if (parseError is not null)
            return parseError;

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var targetDate = JsonArgumentParser.ParseDateOnly(args, "date") ?? today;

        var loggedNames = await HabitToolHelpers.ApplyToHabitsAsync(
            habitRepository, userId, habitIds,
            habit => TryLogHabit(habit, targetDate, ct),
            ct);

        if (loggedNames.Count == 0)
            return new ToolResult(false, Error: "No habits were logged. They may already be completed or not found.");

        return new ToolResult(true, EntityName: string.Join(", ", loggedNames));
    }

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
