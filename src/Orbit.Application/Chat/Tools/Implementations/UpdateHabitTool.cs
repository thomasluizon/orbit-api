using System.Text.Json;
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
    IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "update_habit";

    public string Description =>
        "Update an existing habit's properties. Only include fields you want to change - omit fields to keep their current values. Use emoji to set a user-requested emoji or a sensible emoji for one habit. For bulk emoji changes, use bulk_update_habit_emojis instead of calling this repeatedly. To convert a recurring habit to a one-time task, explicitly set frequency_unit to null. To clear the due time, set due_time to null. Set is_flexible to true for window-based tracking (e.g. '3x per week, any days').";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            habit_id = new { type = JsonSchemaTypes.String, description = "ID of the habit to update" },
            title = new { type = JsonSchemaTypes.String, description = "New title" },
            description = new { type = JsonSchemaTypes.String, description = "New description", nullable = true },
            emoji = new { type = JsonSchemaTypes.String, description = "Emoji used as the habit icon. Set to null to clear.", nullable = true },
            frequency_unit = new
            {
                type = JsonSchemaTypes.String,
                description = "New recurrence unit. Set to null to convert to one-time task.",
                nullable = true,
                @enum = JsonSchemaTypes.FrequencyUnitEnum
            },
            frequency_quantity = new { type = JsonSchemaTypes.Integer, description = "New frequency quantity" },
            interval_weeks = new { type = JsonSchemaTypes.Integer, description = "New repeat interval in weeks. Set to 1 for every week. Minimum 1." },
            days = new
            {
                type = JsonSchemaTypes.Array,
                description = "New weekday schedule",
                items = new { type = JsonSchemaTypes.String }
            },
            due_date = new { type = JsonSchemaTypes.String, description = "New due date (YYYY-MM-DD)" },
            end_date = new { type = JsonSchemaTypes.String, description = "New end date (YYYY-MM-DD). Set to null to clear. Habit stops after this date.", nullable = true },
            due_time = new { type = JsonSchemaTypes.String, description = "New due time (HH:mm). Set to null to clear.", nullable = true },
            due_end_time = new { type = JsonSchemaTypes.String, description = "New end time or deadline (HH:mm). Set null to clear.", nullable = true },
            is_general = new { type = JsonSchemaTypes.Boolean, description = "General habit without a schedule. Children must match their parent." },
            slip_alert_enabled = new { type = JsonSchemaTypes.Boolean, description = "Enable slip alerts. Requires Pro." },
            relative_reminders = BulkHabitToolArguments.RelativeRemindersSchema(),
            is_bad_habit = new { type = JsonSchemaTypes.Boolean, description = "Whether this is a bad habit" },
            is_flexible = new { type = JsonSchemaTypes.Boolean, description = "True for window-based tracking. Cannot have days set." },
            reminder_enabled = new { type = JsonSchemaTypes.Boolean, description = "Enable or disable reminders" },
            reminder_times = new
            {
                type = JsonSchemaTypes.Array,
                description = "New reminder times (minutes before)",
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
                        text = new { type = JsonSchemaTypes.String, description = "Checklist item text" },
                        is_checked = new { type = JsonSchemaTypes.Boolean, description = "Whether checked" }
                    },
                    required = new[] { "text" }
                }
            },
            scheduled_reminders = new
            {
                type = JsonSchemaTypes.Array,
                description = "Absolute-time reminders for habits WITHOUT a due_time. Use INSTEAD of reminder_times when no due_time is set.",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        when = new { type = JsonSchemaTypes.String, description = "day_before or same_day", @enum = JsonSchemaTypes.ScheduledReminderWhenEnum },
                        time = new { type = JsonSchemaTypes.String, description = "HH:mm 24h format" }
                    },
                    required = new[] { "when", "time" }
                }
            }
        },
        required = new[] { "habit_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();

        var (changes, error) = BulkHabitToolArguments.ParseUpdateFields(args, allowHabitId: true);
        if (error is not null)
            return new ToolResult(false, Error: error);
        var result = await mediator.Send(new UpdateHabitFieldsCommand(userId, habitId, changes!), ct);
        if (result.IsFailure)
            return result.ErrorCode == ErrorMessages.HabitNotFound.Code
                ? HabitToolHelpers.HabitNotFoundResult(habitId)
                : ToolResult.FromFailure(result);
        return new ToolResult(true, EntityId: habitId.ToString(), EntityName: result.Value);
    }

    public async Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return Result.Failure("habit_id is required and must be a valid GUID.");

        var (changes, error) = BulkHabitToolArguments.ParseUpdateFields(args, allowHabitId: true);
        if (error is not null)
            return Result.Failure(error);

        var habits = await habitRepository.FindAsync(
            habit => habit.Id == habitId && habit.UserId == userId, ct);
        var habit = habits.FirstOrDefault();
        if (habit is null)
            return Result.Failure($"Habit {habitId} not found.");

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var update = HabitUpdateValidation.ResolveSingleUpdate(habit, changes!, today);
        var validation = await HabitUpdateValidation.ValidateAsync(userId, habit, update, habitRepository, ct);
        if (validation.IsFailure)
            return validation;
        var preview = habit.PreviewUpdate(update);
        return preview.IsFailure ? preview : Result.Success();
    }

}
