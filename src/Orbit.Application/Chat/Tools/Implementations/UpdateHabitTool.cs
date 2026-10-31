using System.Text.Json;
using System.Text.Json.Nodes;
using MediatR;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public class UpdateHabitTool(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IMediator mediator) : IAiTool, IArgumentCheckTool, ITransactionalAiTool
{
    public bool SupportsAtomicExecution(JsonElement arguments) =>
        !arguments.TryGetProperty(ToolArgumentNames.GoalIds, out _);

    public string Name => "update_habit";

    public string Description =>
        "Update habit fields. Use an explicit habit ID supplied by the person directly; no selection lookup or ask is needed. Null keeps values; clear_fields removes them (frequency_unit makes a one-time task). Use is_flexible for windows and bulk tools for multiple habits." + "\n" + HabitModelInstructions.All;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            clear_fields = OptionalToolArguments.ClearFieldsSchema(OptionalToolArguments.HabitClearFields),
            habit_id = new { type = JsonSchemaTypes.String },
            start_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD recurrence anchor." },
            parent_habit_id = new { type = JsonSchemaTypes.String, description = "Existing parent ID." },
            clear_parent = new { type = JsonSchemaTypes.Boolean },
            tag_ids = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } },
            goal_ids = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } },
            title = new { type = JsonSchemaTypes.String },
            description = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "New description. Use clear_fields to clear description." },
            emoji = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "Emoji used as the habit icon. Use clear_fields to clear." },
            frequency_unit = new
            {
                type = new[] { JsonSchemaTypes.String, "null" },
                description = "New recurrence unit. Clear frequency_unit via clear_fields for a one-time task.",
                @enum = JsonSchemaTypes.FrequencyUnitEnum
            },
            frequency_quantity = new { type = JsonSchemaTypes.Integer, description = "New frequency quantity" },
            interval_weeks = new { type = JsonSchemaTypes.Integer, description = "Repeat interval in weeks, minimum 1." },
            days = new
            {
                type = JsonSchemaTypes.Array,
                items = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.WeekdayEnum }
            },
            due_date = new { type = JsonSchemaTypes.String, description = "New due date (YYYY-MM-DD)" },
            end_date = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "YYYY-MM-DD cutoff; clear via clear_fields." },
            due_time = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "New due time (HH:mm). Use clear_fields to clear." },
            due_end_time = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "End time or deadline (HH:mm). Use clear_fields to clear." },
            is_general = new { type = JsonSchemaTypes.Boolean, description = "General habit without a schedule. Children must match their parent." },
            slip_alert_enabled = new { type = JsonSchemaTypes.Boolean, description = "Enable slip alerts. Requires Pro." },
            relative_reminders = BulkHabitToolArguments.RelativeRemindersSchema(),
            repeat_reminder = RepeatReminderToolArguments.RepeatReminderSchema(forUpdate: true),
            is_bad_habit = new { type = JsonSchemaTypes.Boolean, description = HabitModelInstructions.Polarity },
            is_flexible = new { type = JsonSchemaTypes.Boolean, description = "True for window-based tracking. Cannot have days set." },
            reminder_enabled = new { type = JsonSchemaTypes.Boolean, description = "Enable or disable reminders" },
            reminder_times = new
            {
                type = JsonSchemaTypes.Array,
                description = "Minutes before due_time.",
                items = new { type = JsonSchemaTypes.Integer }
            },
            checklist_items = new
            {
                type = JsonSchemaTypes.Array,
                description = "Replace checklist items",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        text = new { type = JsonSchemaTypes.String },
                        is_checked = new { type = JsonSchemaTypes.Boolean, description = "Default: false." }
                    },
                    required = new[] { "text" }
                }
            },
            scheduled_reminders = new
            {
                type = JsonSchemaTypes.Array,
                description = "Absolute reminders when due_time is absent. Use instead of reminder_times.",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        when = new { type = JsonSchemaTypes.String, description = "Reminder day.", @enum = JsonSchemaTypes.ScheduledReminderWhenEnum },
                        time = new { type = JsonSchemaTypes.String, description = "HH:mm" }
                    },
                    required = new[] { "when", "time" }
                }
            }
        },
        required = new[] { "habit_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());

        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();

        var (changes, error) = BulkHabitToolArguments.ParseUpdateFields(FieldArguments(args), allowHabitId: true);
        if (error is not null)
            return new ToolResult(false, Error: error);
        var result = await mediator.Send(new UpdateHabitFieldsCommand(userId, habitId, changes!,
            GoalIds: JsonArgumentParser.ParseGuidArray(args, "goal_ids"), TagIds: JsonArgumentParser.ParseGuidArray(args, "tag_ids"),
            ParentId: Guid.TryParse(JsonArgumentParser.GetOptionalString(args, "parent_habit_id"), out var parentId) ? parentId : null,
            ClearParent: JsonArgumentParser.GetOptionalBool(args, "clear_parent")), ct);
        if (result.IsFailure)
            return result.ErrorCode == ErrorMessages.HabitNotFound.Code
                ? HabitToolHelpers.HabitNotFoundResult(habitId)
                : ToolResult.FromFailure(result);
        return new ToolResult(true, EntityId: habitId.ToString(), EntityName: result.Value);
    }

    private static JsonElement FieldArguments(JsonElement args)
    {
        var node = JsonNode.Parse(args.GetRawText())!.AsObject();
        foreach (var field in new[] { "goal_ids", "tag_ids", "parent_habit_id", "clear_parent" }) node.Remove(field);
        return JsonSerializer.SerializeToElement(node);
    }

    public async Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());

        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return Result.Failure("habit_id is required and must be a valid GUID.");

        var (changes, error) = BulkHabitToolArguments.ParseUpdateFields(FieldArguments(args), allowHabitId: true);
        if (error is not null)
            return Result.Failure(error);

        var habits = await habitRepository.FindAsync(
            habit => habit.Id == habitId && habit.UserId == userId, ct);
        var habit = habits.FirstOrDefault();
        if (habit is null)
            return Result.Failure($"Habit {habitId} not found.");

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var resolved = HabitUpdateValidation.ResolveSingleUpdate(habit, changes!, today);
        if (resolved.IsFailure)
            return resolved.PropagateError();
        var update = resolved.Value;
        var validation = await HabitUpdateValidation.ValidateAsync(userId, habit, update, habitRepository, ct);
        if (validation.IsFailure)
            return validation;
        var preview = habit.PreviewUpdate(update);
        return preview.IsFailure ? preview : Result.Success();
    }

}
