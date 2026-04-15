using System.Globalization;
using System.Text.Json;
using MediatR;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

public class CreateSubHabitTool(
    IMediator mediator) : IAiTool
{
    public string Name => "create_sub_habit";

    public string Description =>
        "Create a sub-habit under an existing parent habit. Optionally pass 'icon' with an emoji to give the sub-habit its own visual identity.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            parent_habit_id = new { type = JsonSchemaTypes.String, description = "ID of the existing parent habit" },
            title = new { type = JsonSchemaTypes.String, description = "Name of the new sub-habit" },
            description = new { type = JsonSchemaTypes.String, description = "Optional description" },
            icon = new { type = JsonSchemaTypes.String, description = "Optional emoji to represent this sub-habit. Max 32 characters.", nullable = true },
            frequency_unit = new
            {
                type = JsonSchemaTypes.String,
                description = "Override parent frequency",
                @enum = JsonSchemaTypes.FrequencyUnitEnum
            },
            frequency_quantity = new { type = JsonSchemaTypes.Integer, description = "Override parent frequency quantity" },
            days = new
            {
                type = JsonSchemaTypes.Array,
                description = "Specific weekdays, only when frequency_quantity is 1",
                items = new { type = JsonSchemaTypes.String }
            },
            due_time = new { type = JsonSchemaTypes.String, description = "HH:mm 24h format" },
            due_end_time = new { type = JsonSchemaTypes.String, description = "HH:mm 24h format end time" },
            is_bad_habit = new { type = JsonSchemaTypes.Boolean, description = "True for habits the user wants to AVOID" },
            reminder_enabled = new { type = JsonSchemaTypes.Boolean, description = "Set true for reminder notifications" },
            reminder_times = new { type = JsonSchemaTypes.Array, description = "Minutes before dueTime to send reminders", items = new { type = JsonSchemaTypes.Integer } },
            slip_alert_enabled = new { type = JsonSchemaTypes.Boolean, description = "Enable slip alert notifications" },
            is_flexible = new { type = JsonSchemaTypes.Boolean, description = "True for flexible frequency" },
            due_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD override for due date" },
            scheduled_reminders = new
            {
                type = JsonSchemaTypes.Array,
                description = "Absolute-time reminders for habits WITHOUT a due_time",
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
        required = new[] { "parent_habit_id", "title" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("parent_habit_id", out var parentIdEl) ||
            !Guid.TryParse(parentIdEl.GetString(), out var parentHabitId))
            return new ToolResult(false, Error: "parent_habit_id is required and must be a valid GUID.");

        if (!args.TryGetProperty("title", out var titleEl) || string.IsNullOrWhiteSpace(titleEl.GetString()))
            return new ToolResult(false, Error: "title is required.");

        var title = titleEl.GetString() ?? string.Empty;

        var (frequencyUnit, frequencyQuantity) = ParseScheduleOptions(args);
        var days = JsonArgumentParser.ParseDays(args);
        var (dueTime, dueEndTime) = ParseTimeOptions(args);
        var (isBadHabit, reminderEnabled, slipAlertEnabled, isFlexible) = ParseBooleanFlags(args);
        var reminderTimes = JsonArgumentParser.ParseIntArray(args, "reminder_times");
        var dueDate = JsonArgumentParser.ParseDateOnly(args, "due_date");
        var scheduledReminders = JsonArgumentParser.ParseScheduledReminders(args);
        string? description = JsonArgumentParser.GetOptionalString(args, "description");
        string? icon = JsonArgumentParser.GetOptionalString(args, "icon");

        var result = await mediator.Send(
            new Orbit.Application.Habits.Commands.CreateSubHabitCommand(
                userId,
                parentHabitId,
                title,
                description,
                frequencyUnit,
                frequencyQuantity,
                IsBadHabit: isBadHabit,
                DueDate: dueDate,
                Options: new Orbit.Application.Habits.Commands.HabitCommandOptions(
                    Days: days,
                    DueTime: dueTime,
                    DueEndTime: dueEndTime,
                    ReminderEnabled: reminderEnabled,
                    ReminderTimes: reminderTimes,
                    SlipAlertEnabled: slipAlertEnabled,
                    IsFlexible: isFlexible,
                    ScheduledReminders: scheduledReminders),
                Icon: icon), ct);

        if (result.IsFailure)
            return new ToolResult(false, Error: result.Error);

        return new ToolResult(true, EntityId: result.Value.ToString(), EntityName: title);
    }

    private static (FrequencyUnit? Unit, int? Quantity) ParseScheduleOptions(JsonElement args)
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

        return (frequencyUnit, frequencyQuantity);
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
