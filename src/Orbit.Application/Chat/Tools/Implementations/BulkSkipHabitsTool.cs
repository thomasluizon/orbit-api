using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class BulkSkipHabitsTool(
    IMediator mediator,
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    BulkHabitReplayPlanner replayPlanner) : IAiTool
{
    public string Name => "bulk_skip_habits";

    public string Description =>
        "Skip multiple habits from the complete server-side set matching a filter in one operation. Use only for habits the user explicitly described skipping. Reports applied, matched, skipped, and partial counts.";

    public object GetParameterSchema() => BulkHabitToolArguments.ActionFilterSchema(
        "Legacy array of habit IDs to skip.",
        "Date to skip in YYYY-MM-DD format. Defaults to today.");

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        replayPlanner.ExecuteAsync(
            args, userId, habitRepository, userDateService, typeof(BulkSkipHabitsCommand).FullName!,
            (id, date) => new BulkSkipItem(id, date),
            (items, cancellationToken) => mediator.Send(
                new BulkSkipHabitsCommand(userId, items),
                cancellationToken),
            result => result.Results.Count(item => item.Status == BulkItemStatus.Success),
            "Skipped",
            "No matching habits found to skip.",
            ct);
}
