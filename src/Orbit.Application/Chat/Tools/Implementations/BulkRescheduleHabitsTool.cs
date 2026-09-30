using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class BulkRescheduleHabitsTool(IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "bulk_reschedule_habits";

    public string Description =>
        "Reschedule the complete server-side set of habits matching a filter to one due date. Use one call for every multi-habit reschedule instead of repeating update_habit.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            filter = BulkHabitToolArguments.FilterSchema(),
            due_date = new { type = JsonSchemaTypes.String, description = "New due date in YYYY-MM-DD format." }
        },
        required = new[] { "filter", "due_date" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public async Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await ExecuteCoreAsync(args, userId, ct, checkOnly: true);
        return result.Success ? Orbit.Domain.Common.Result.Success() : Orbit.Domain.Common.Result.Failure(result.Error!);
    }

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        if (args.TryGetProperty("revised_items", out var revisedItems))
            return await BulkUpdateHabitsTool.ExecuteRevisedAsync(mediator,
                revisedItems, userId, "Rescheduled", ct, checkOnly);

        var (filter, filterError) = BulkHabitToolArguments.ParseRequiredFilter(args);
        if (filterError is not null)
            return new ToolResult(false, Error: filterError);
        if (!args.TryGetProperty("due_date", out var dueDateElement)
            || dueDateElement.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(dueDateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dueDate))
        {
            return new ToolResult(false, Error: "due_date must be a date in YYYY-MM-DD format.");
        }

        var changes = new BulkHabitChanges(HasDueDate: true, DueDate: dueDate);
        var command = new BulkUpdateHabitsCommand(userId, filter!, changes);
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);
        var result = await mediator.Send(command, ct);
        if (result.IsFailure)
            return ToolResult.FromFailure(result);
        return BulkUpdateHabitsTool.BuildResult(result.Value, "Rescheduled");
    }
}
