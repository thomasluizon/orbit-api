using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class BulkLogHabitsTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    BulkHabitReplayPlanner replayPlanner) : IAiTool
{
    public string Name => "bulk_log_habits";

    public string Description =>
        "Log multiple habits from the complete server-side set matching a filter in one operation. Use only for habits the user explicitly described completing. Reports applied, matched, skipped, and partial counts.";

    public object GetParameterSchema() => BulkHabitToolArguments.ActionFilterSchema(
        "Legacy array of habit IDs to log as completed.",
        "Date to log for in YYYY-MM-DD format. Defaults to today.");

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        replayPlanner.ExecuteAsync(
            args, userId, habitRepository, userDateService, typeof(BulkLogHabitsCommand).FullName!,
            (id, date) => new BulkLogItem(id, date),
            (items, cancellationToken) => mediator.Send(
                new BulkLogHabitsCommand(userId, items),
                cancellationToken),
            result => result.Results.Count(item => item.Status == BulkItemStatus.Success && item.LogId.HasValue),
            "Logged",
            "No matching habits found to log.",
            ct);
}
