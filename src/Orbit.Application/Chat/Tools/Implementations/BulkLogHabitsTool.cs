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
    BulkHabitReplayPlanner replayPlanner) : IAiTool, IArgumentCheckTool
{
    public string Name => "bulk_log_habits";

    public string Description =>
        "Log multiple habits from the complete server-side set matching a filter in one operation. Use only for habits the user explicitly described completing. Reports applied, matched, skipped, and partial counts.";

    public object GetParameterSchema() => BulkHabitToolArguments.ActionFilterSchema(
        "Legacy array of habit IDs to log as completed.",
        "Date to log for in YYYY-MM-DD format. Defaults to today.");

    public async Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        IReadOnlyList<BulkLogItem> items;
        if (args.TryGetProperty("revised_items", out _))
        {
            var (revised, error) = BulkHabitToolArguments.ParseRevisedDatedItems(args);
            if (error is not null)
                return Orbit.Domain.Common.Result.Failure(error);
            items = revised!.Select(value => new BulkLogItem(value.HabitId, value.Date)).ToList();
        }
        else
        {
            var (filter, error) = BulkHabitToolArguments.ParseActionFilter(args);
            if (error is not null)
                return Orbit.Domain.Common.Result.Failure(error);
            var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter!, ct);
            var date = JsonArgumentParser.ParseDateOnly(args, "date") ?? await userDateService.GetUserTodayAsync(userId, ct);
            items = habits.Select(h => new BulkLogItem(h.Id, date)).ToList();
        }
        if (items.Count == 0)
            return Orbit.Domain.Common.Result.Failure("No matching habits found.");
        foreach (var chunk in items.Chunk(Orbit.Application.Common.AppConstants.MaxBulkOperationSize))
        {
            var result = await mediator.Send(new CheckChatCommandQuery(new BulkLogHabitsCommand(userId, chunk)), ct);
            if (result.IsFailure)
                return result;
        }
        return Orbit.Domain.Common.Result.Success();
    }

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (args.TryGetProperty("revised_items", out _))
        {
            var (revised, revisedError) = BulkHabitToolArguments.ParseRevisedDatedItems(args);
            if (revisedError is not null)
                return new ToolResult(false, Error: revisedError);
            return await BulkUpdateHabitsTool.ExecuteInChunksAsync(
                revised!.Select(item => new BulkLogItem(item.HabitId, item.Date)).ToList(),
                (items, token) => mediator.Send(new BulkLogHabitsCommand(userId, items), token),
                result => result.Results.Count(item => item.Status == BulkItemStatus.Success && item.LogId.HasValue),
                "Logged", ct);
        }

        return await replayPlanner.ExecuteAsync(
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
}
