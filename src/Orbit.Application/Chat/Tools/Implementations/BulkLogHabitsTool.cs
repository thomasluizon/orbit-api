using System.Globalization;
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

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (filter, filterError) = BulkHabitToolArguments.ParseActionFilter(args);
        if (filterError is not null)
            return new ToolResult(false, Error: filterError);
        var dateError = ValidateDate(args);
        if (dateError is not null)
            return new ToolResult(false, Error: dateError);

        var plan = await replayPlanner.GetOrCreateAsync(userId, typeof(BulkLogHabitsCommand).FullName!, async token =>
        {
            var date = await ResolveDateAsync(args, userId, token);
            var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter!, token);
            return new BulkHabitReplayPlan(date, habits.Select(habit => habit.Id).ToArray());
        }, ct);
        if (plan.HabitIds.Count == 0)
            return new ToolResult(false, Error: "No matching habits found to log.");

        return await BulkUpdateHabitsTool.ExecuteInChunksAsync(
            plan.HabitIds.Select(id => new BulkLogItem(id, plan.Date)).ToList(),
            (items, cancellationToken) => mediator.Send(
                new BulkLogHabitsCommand(userId, items),
                cancellationToken),
            result => result.Results.Count(item => item.Status == BulkItemStatus.Success && item.LogId.HasValue),
            "Logged",
            ct);
    }

    private static string? ValidateDate(JsonElement args)
    {
        if (!args.TryGetProperty("date", out var dateElement) || dateElement.ValueKind == JsonValueKind.Null)
            return null;
        return dateElement.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(dateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? null
            : "date must use YYYY-MM-DD format.";
    }

    private async Task<DateOnly> ResolveDateAsync(
        JsonElement args,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!args.TryGetProperty("date", out var dateElement) || dateElement.ValueKind == JsonValueKind.Null)
            return await userDateService.GetUserTodayAsync(userId, cancellationToken);
        return DateOnly.ParseExact(dateElement.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
