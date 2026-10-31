using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Chat.Tools.Implementations;

public sealed class BulkRescheduleHabitsTool(IMediator mediator) : IAiTool, IArgumentCheckTool, ITransactionalAiTool
{
    public bool SupportsAtomicExecution(JsonElement arguments) => true;

    public string Name => "bulk_reschedule_habits";

    public string Description =>
        "Propose rescheduling in one editable approval preview. Use filter and due_date for a shared date, "
        + "or revised_items for individual dates, omitting filter and due_date. "
        + RestartPlanInstructions.English;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            restart_plan = new
            {
                type = JsonSchemaTypes.Boolean,
                description = "True for a restart plan covering every remaining due and overdue habit after a fresh query_habits in this turn. False for ordinary rescheduling."
            },
            filter = BulkHabitToolArguments.FilterSchema(),
            due_date = new { type = JsonSchemaTypes.String, description = "New due date in YYYY-MM-DD format." },
            revised_items = new
            {
                type = JsonSchemaTypes.Array,
                description = "Complete plan with one entry per habit, including habits kept today. Overrides the shared filter and date.",
                minItems = 1,
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        habit_id = new { type = JsonSchemaTypes.String },
                        updates = new
                        {
                            type = JsonSchemaTypes.Object,
                            properties = new { due_date = new { type = JsonSchemaTypes.String, description = "Proposed date in YYYY-MM-DD format." } },
                            required = new[] { "due_date" }
                        }
                    },
                    required = new[] { "habit_id", "updates" }
                }
            }
        },
        required = new[] { "restart_plan" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public async Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var result = await ExecuteCoreAsync(args, userId, ct, checkOnly: true);
        return result.Success ? Orbit.Domain.Common.Result.Success() : Orbit.Domain.Common.Result.Failure(result.Error!, result.ErrorCode);
    }

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        if (args.TryGetProperty("revised_items", out var revisedItems))
            return await BulkUpdateHabitsTool.ExecuteRevisedAsync(mediator,
                revisedItems, userId, "Rescheduled", ct, checkOnly, requireAll: true);

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
