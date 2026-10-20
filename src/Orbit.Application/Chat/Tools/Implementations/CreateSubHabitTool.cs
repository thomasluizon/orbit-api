using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

public class CreateSubHabitTool(
    IMediator mediator) : IAiTool, IArgumentCheckTool
{
    public string Name => "create_sub_habit";

    public int Order => 1;

    public string Description =>
        "Create a sub-habit under an existing parent, with a requested or relevant emoji.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            parent_habit_id = new { type = JsonSchemaTypes.String },
            title = new { type = JsonSchemaTypes.String },
            description = new { type = JsonSchemaTypes.String },
            emoji = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "Requested or relevant icon emoji." },
            frequency_unit = new
            {
                type = JsonSchemaTypes.String,
                description = "Override parent frequency. Use OneTime for a one-time child; null inherits.",
                nullable = true,
                @enum = JsonSchemaTypes.FrequencyOrOneTimeEnum
            },
            frequency_quantity = new { type = JsonSchemaTypes.Integer, description = "Override parent frequency quantity" },
            interval_weeks = new { type = JsonSchemaTypes.Integer, description = "Override parent repeat interval in weeks. Minimum 1." },
            days = new
            {
                type = JsonSchemaTypes.Array,
                description = "Specific weekdays, only when frequency_quantity is 1",
                items = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.WeekdayEnum }
            },
            due_time = new { type = JsonSchemaTypes.String, description = "HH:mm" },
            due_end_time = new { type = JsonSchemaTypes.String, description = "HH:mm end time" },
            is_bad_habit = new { type = JsonSchemaTypes.Boolean, description = "True for habits the user wants to AVOID" },
            reminder_enabled = new { type = JsonSchemaTypes.Boolean, description = "Set true for reminder notifications" },
            reminder_times = new { type = JsonSchemaTypes.Array, description = "Minutes before dueTime to send reminders", items = new { type = JsonSchemaTypes.Integer } },
            slip_alert_enabled = new { type = JsonSchemaTypes.Boolean, description = "Enable slip alert notifications" },
            is_flexible = new { type = JsonSchemaTypes.Boolean, description = "True for flexible frequency" },
            end_date = new { type = JsonSchemaTypes.String, description = "End date (YYYY-MM-DD)", nullable = true },
            relative_reminders = BulkHabitToolArguments.RelativeRemindersSchema(),
            checklist_items = BulkHabitToolArguments.ChecklistItemsSchema(),
            tag_ids = new { type = JsonSchemaTypes.Array, description = "Existing tag IDs", items = new { type = JsonSchemaTypes.String } },
            due_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD override for due date" },
            scheduled_reminders = BulkHabitToolArguments.ScheduledRemindersSchema()
        },
        required = new[] { "parent_habit_id", "title" }
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ExecuteCoreAsync(args, userId, ct, checkOnly: false);

    public Task<Orbit.Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        ChatToolArgumentCheck.CheckAsync(this, args, () => ExecuteCoreAsync(args, userId, ct, checkOnly: true));

    private async Task<ToolResult> ExecuteCoreAsync(JsonElement args, Guid userId, CancellationToken ct, bool checkOnly)
    {
        if (!args.TryGetProperty("parent_habit_id", out var parentIdEl) ||
            !Guid.TryParse(parentIdEl.GetString(), out var parentHabitId))
            return new ToolResult(false, Error: "parent_habit_id is required and must be a valid GUID.");

        if (!args.TryGetProperty("title", out var titleEl) || string.IsNullOrWhiteSpace(titleEl.GetString()))
            return new ToolResult(false, Error: "title is required.");

        var title = titleEl.GetString() ?? string.Empty;
        if (args.TryGetProperty("tag_ids", out var tagIds) && (tagIds.ValueKind != JsonValueKind.Array
            || tagIds.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out _))))
            return new ToolResult(false, Error: "tag_ids must contain valid GUIDs.");
        var fieldError = BulkHabitToolArguments.CheckHabitFields(args);
        if (fieldError is not null)
            return new ToolResult(false, Error: fieldError);

        var (frequencyUnit, frequencyQuantity, intervalWeeks) = ParseScheduleOptions(args);
        var days = JsonArgumentParser.ParseDays(args);
        var (dueTime, dueEndTime) = ParseTimeOptions(args);
        var (isBadHabit, reminderEnabled, slipAlertEnabled, isFlexible) = ParseBooleanFlags(args);
        var dueDate = JsonArgumentParser.ParseDateOnly(args, "due_date");
        var reminderTimes = JsonArgumentParser.ParseIntArray(args, "reminder_times");
        var scheduledReminders = JsonArgumentParser.ParseScheduledReminders(args);
        string? description = JsonArgumentParser.GetOptionalString(args, "description");
        string? emoji = JsonArgumentParser.GetOptionalString(args, "emoji");

        var command = new Orbit.Application.Habits.Commands.CreateSubHabitCommand(
                userId,
                parentHabitId,
                title,
                description,
                frequencyUnit,
                frequencyQuantity,
                IsBadHabit: isBadHabit,
                DueDate: dueDate,
                Emoji: emoji,
                TagIds: JsonArgumentParser.ParseGuidArray(args, "tag_ids"),
                InheritParentFrequency: !args.TryGetProperty("frequency_unit", out var frequencyValue)
                    || frequencyValue.ValueKind != JsonValueKind.Null && frequencyValue.GetString() != "OneTime",
                IntervalWeeks: intervalWeeks,
                Options: new Orbit.Application.Habits.Commands.HabitCommandOptions(
                    Days: days,
                    DueTime: dueTime,
                    DueEndTime: dueEndTime,
                    ReminderEnabled: reminderEnabled,
                    ReminderTimes: reminderTimes,
                    SlipAlertEnabled: slipAlertEnabled,
                    IsFlexible: isFlexible,
                    ScheduledReminders: scheduledReminders,
                    RelativeReminders: JsonArgumentParser.ParseRelativeReminders(args),
                    ChecklistItems: JsonArgumentParser.ParseChecklistItems(args),
                    EndDate: JsonArgumentParser.ParseDateOnly(args, "end_date")));
        if (checkOnly)
            return await ChatToolArgumentCheck.CheckCommandAsync(mediator, command, ct);

        var result = await mediator.Send(command, ct);

        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return new ToolResult(true, EntityId: result.Value.ToString(), EntityName: title);
    }

    private static (FrequencyUnit? Unit, int? Quantity, int? IntervalWeeks) ParseScheduleOptions(JsonElement args)
    {
        FrequencyUnit? frequencyUnit = null;
        if (args.TryGetProperty("frequency_unit", out var fuEl) && fuEl.ValueKind == JsonValueKind.String
            && Enum.TryParse<FrequencyUnit>(fuEl.GetString(), ignoreCase: true, out var fu))
        {
            frequencyUnit = fu;
        }

        int? frequencyQuantity = null;
        if (args.TryGetProperty("frequency_quantity", out var fqEl) && fqEl.ValueKind == JsonValueKind.Number)
            frequencyQuantity = fqEl.GetInt32();
        frequencyQuantity ??= frequencyUnit is not null ? 1 : null;

        return (frequencyUnit, frequencyQuantity, JsonArgumentParser.GetOptionalInt(args, "interval_weeks"));
    }

    private static (TimeOnly? DueTime, TimeOnly? DueEndTime) ParseTimeOptions(JsonElement args)
    {
        TimeOnly? dueTime = null;
        if (args.TryGetProperty("due_time", out var dtEl) && dtEl.ValueKind == JsonValueKind.String
            && TimeOnly.TryParse(dtEl.GetString(), CultureInfo.InvariantCulture, out var time))
        {
            dueTime = time;
        }

        TimeOnly? dueEndTime = null;
        if (args.TryGetProperty("due_end_time", out var detEl) && detEl.ValueKind == JsonValueKind.String
            && TimeOnly.TryParse(detEl.GetString(), CultureInfo.InvariantCulture, out var endTime))
        {
            dueEndTime = endTime;
        }

        return (dueTime, dueEndTime);
    }

    private static (bool IsBadHabit, bool ReminderEnabled, bool SlipAlertEnabled, bool IsFlexible) ParseBooleanFlags(JsonElement args)
    {
        bool isBadHabit = args.TryGetProperty("is_bad_habit", out var ibhEl) && ibhEl.ValueKind == JsonValueKind.True;
        bool reminderEnabled = args.TryGetProperty("reminder_enabled", out var reEl) && reEl.ValueKind == JsonValueKind.True;
        bool slipAlertEnabled = args.TryGetProperty("slip_alert_enabled", out var saEl) && saEl.ValueKind == JsonValueKind.True;
        bool isFlexible = args.TryGetProperty("is_flexible", out var ifEl) && ifEl.ValueKind == JsonValueKind.True;
        return (isBadHabit, reminderEnabled, slipAlertEnabled, isFlexible);
    }
}
