using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

public class UpdateHabitTool(
    IGenericRepository<Habit> habitRepository) : IAiTool
{
    public string Name => "update_habit";

    public string Description =>
        "Update an existing habit's properties. Only include fields you want to change - omit fields to keep their current values. To convert a recurring habit to a one-time task, explicitly set frequency_unit to null. To clear the due time, set due_time to null. Set is_flexible to true for window-based tracking (e.g. '3x per week, any days').";

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
            days = new
            {
                type = JsonSchemaTypes.Array,
                description = "New weekday schedule",
                items = new { type = JsonSchemaTypes.String }
            },
            due_date = new { type = JsonSchemaTypes.String, description = "New due date (YYYY-MM-DD)" },
            end_date = new { type = JsonSchemaTypes.String, description = "New end date (YYYY-MM-DD). Set to null to clear. Habit stops after this date.", nullable = true },
            due_time = new { type = JsonSchemaTypes.String, description = "New due time (HH:mm). Set to null to clear.", nullable = true },
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
        if (!args.TryGetProperty("habit_id", out var habitIdEl) ||
            !Guid.TryParse(habitIdEl.GetString(), out var habitId))
            return new ToolResult(false, Error: "habit_id is required and must be a valid GUID.");

        var habit = await habitRepository.FindOneTrackedAsync(
            h => h.Id == habitId && h.UserId == userId,
            cancellationToken: ct);

        if (habit is null)
            return new ToolResult(false, Error: $"Habit {habitId} not found.");

        var updateParams = ResolveUpdateParams(args, habit);

        var result = habit.Update(updateParams);
        if (result.IsFailure)
            return new ToolResult(false, Error: result.Error);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }

    /// <summary>
    /// Resolve each field: absent = keep existing, null = clear, value = update.
    /// Extracted to reduce ExecuteAsync cognitive complexity.
    /// </summary>
    private static HabitUpdateParams ResolveUpdateParams(JsonElement args, Habit habit)
    {
        var title = ResolveTitle(args, habit);
        var description = ResolveDescription(args, habit);
        var (frequencyUnit, frequencyQuantity) = ResolveFrequency(args, habit);
        var days = ResolveDays(args, habit);
        var isBadHabit = ResolveBoolField(args, "is_bad_habit", habit.IsBadHabit);
        var dueDate = ResolveDueDate(args, habit);
        var dueTime = ResolveDueTime(args, habit);
        var (endDate, clearEndDate) = ResolveEndDate(args);

        return new HabitUpdateParams(
            title, description, frequencyUnit, frequencyQuantity, days, isBadHabit, dueDate,
            DueTime: dueTime,
            ReminderEnabled: ResolveOptionalBool(args, "reminder_enabled"),
            ReminderTimes: ResolveOptionalArray(args, "reminder_times"),
            ChecklistItems: ResolveOptionalChecklist(args),
            IsFlexible: ResolveOptionalBool(args, "is_flexible"),
            EndDate: endDate,
            ClearEndDate: clearEndDate,
            ScheduledReminders: ResolveOptionalScheduledReminders(args),
            Emoji: ResolveEmoji(args, habit));
    }

    private static string ResolveTitle(JsonElement args, Habit habit) =>
        JsonArgumentParser.PropertyExists(args, "title")
            ? JsonArgumentParser.GetNullableString(args, "title") ?? habit.Title
            : habit.Title;

    private static string? ResolveDescription(JsonElement args, Habit habit) =>
        JsonArgumentParser.PropertyExists(args, "description")
            ? JsonArgumentParser.GetNullableString(args, "description")
            : habit.Description;

    private static string? ResolveEmoji(JsonElement args, Habit habit) =>
        JsonArgumentParser.PropertyExists(args, "emoji")
            ? JsonArgumentParser.GetNullableString(args, "emoji")
            : habit.Emoji;

    private static (FrequencyUnit? Unit, int? Quantity) ResolveFrequency(JsonElement args, Habit habit)
    {
        FrequencyUnit? frequencyUnit;
        if (JsonArgumentParser.PropertyExists(args, "frequency_unit"))
        {
            var fuStr = JsonArgumentParser.GetNullableString(args, "frequency_unit");
            frequencyUnit = fuStr is not null && Enum.TryParse<FrequencyUnit>(fuStr, ignoreCase: true, out var fu)
                ? fu
                : null;
        }
        else
        {
            frequencyUnit = habit.FrequencyUnit;
        }

        var frequencyQuantity = JsonArgumentParser.PropertyExists(args, "frequency_quantity")
            ? JsonArgumentParser.GetOptionalInt(args, "frequency_quantity") ?? habit.FrequencyQuantity
            : habit.FrequencyQuantity;

        return (frequencyUnit, frequencyQuantity);
    }

    private static List<DayOfWeek>? ResolveDays(JsonElement args, Habit habit) =>
        JsonArgumentParser.PropertyExists(args, "days")
            ? JsonArgumentParser.ParseDays(args)
            : habit.Days.ToList();

    private static bool ResolveBoolField(JsonElement args, string prop, bool currentValue) =>
        JsonArgumentParser.PropertyExists(args, prop)
            ? JsonArgumentParser.GetOptionalBool(args, prop) ?? currentValue
            : currentValue;

    private static DateOnly? ResolveDueDate(JsonElement args, Habit habit) =>
        JsonArgumentParser.PropertyExists(args, "due_date")
            ? JsonArgumentParser.ParseDateOnly(args, "due_date") ?? habit.DueDate
            : habit.DueDate;

    private static TimeOnly? ResolveDueTime(JsonElement args, Habit habit)
    {
        if (!JsonArgumentParser.PropertyExists(args, "due_time"))
            return habit.DueTime;

        var dtStr = JsonArgumentParser.GetNullableString(args, "due_time");
        return dtStr is not null ? JsonArgumentParser.ParseTimeOnlyFromString(dtStr) : null;
    }

    private static (DateOnly? EndDate, bool? ClearEndDate) ResolveEndDate(JsonElement args)
    {
        if (!JsonArgumentParser.PropertyExists(args, "end_date"))
            return (null, null);

        var edStr = JsonArgumentParser.GetNullableString(args, "end_date");
        if (edStr is null)
            return (null, true);

        var endDate = DateOnly.TryParseExact(edStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ed) ? ed : (DateOnly?)null;
        return (endDate, null);
    }

    private static bool? ResolveOptionalBool(JsonElement args, string prop) =>
        JsonArgumentParser.PropertyExists(args, prop)
            ? JsonArgumentParser.GetOptionalBool(args, prop)
            : null;

    private static List<int>? ResolveOptionalArray(JsonElement args, string prop) =>
        JsonArgumentParser.PropertyExists(args, prop)
            ? JsonArgumentParser.ParseIntArray(args, prop)
            : null;

    private static List<ChecklistItem>? ResolveOptionalChecklist(JsonElement args) =>
        JsonArgumentParser.PropertyExists(args, "checklist_items")
            ? JsonArgumentParser.ParseChecklistItems(args)
            : null;

    private static List<ScheduledReminderTime>? ResolveOptionalScheduledReminders(JsonElement args) =>
        JsonArgumentParser.PropertyExists(args, "scheduled_reminders")
            ? JsonArgumentParser.ParseScheduledReminders(args)
            : null;
}
