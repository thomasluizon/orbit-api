using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class BulkLogHabitsTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService) : IAiTool
{
    public string Name => "bulk_log_habits";

    public string Description =>
        "Log multiple habits as completed for today in a single operation. Use this only for habits the user EXPLICITLY mentioned completing - never include extra habits that share a tag, parent, routine, or theme but were not named.";

    public object GetParameterSchema() => HabitToolHelpers.BulkHabitActionSchema(
        "Array of habit IDs to log as completed",
        "Date to log for in YYYY-MM-DD format (defaults to today)");

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (habitIds, parseError) = HabitToolHelpers.ParseHabitIds(args);
        if (parseError is not null)
            return parseError;

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var targetDate = JsonArgumentParser.ParseDateOnly(args, "date") ?? today;
        var habits = await habitRepository.FindAsync(
            h => habitIds.Contains(h.Id) && h.UserId == userId,
            ct);
        if (habits.Count == 0)
            return new ToolResult(false, Error: "No habits were logged. They may already be completed or not found.");

        var result = await mediator.Send(
            new BulkLogHabitsCommand(userId, habitIds.Select(id => new BulkLogItem(id, targetDate)).ToList()),
            ct);
        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        var loggedIds = result.Value.Results
            .Where(item => item.Status == BulkItemStatus.Success && item.LogId.HasValue)
            .Select(item => item.HabitId)
            .ToHashSet();
        var loggedTitles = habits
            .Where(habit => loggedIds.Contains(habit.Id))
            .Select(habit => habit.Title)
            .ToList();

        return loggedTitles.Count == 0
            ? new ToolResult(false, Error: "No habits were logged. They may already be completed or not found.")
            : new ToolResult(true, EntityName: string.Join(", ", loggedTitles));
    }
}
