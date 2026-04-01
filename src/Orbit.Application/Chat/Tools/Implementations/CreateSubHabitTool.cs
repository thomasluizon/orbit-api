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
        "Create a sub-habit under an existing parent habit.";

    public object GetParameterSchema() => new
    {
        type = "object",
        properties = new
        {
            parent_habit_id = new { type = "string", description = "ID of the existing parent habit" },
            title = new { type = "string", description = "Name of the new sub-habit" },
            description = new { type = "string", description = "Optional description" },
            frequency_unit = new
            {
                type = "string",
                description = "Override parent frequency",
                @enum = new[] { "Day", "Week", "Month", "Year" }
            },
            frequency_quantity = new { type = "integer", description = "Override parent frequency quantity" },
            days = new
            {
                type = "array",
                description = "Specific weekdays, only when frequency_quantity is 1",
                items = new { type = "string" }
            },
            due_time = new { type = "string", description = "HH:mm 24h format" },
            due_end_time = new { type = "string", description = "HH:mm 24h format end time" },
            is_bad_habit = new { type = "boolean", description = "True for habits the user wants to AVOID" },
            reminder_enabled = new { type = "boolean", description = "Set true for reminder notifications" },
            reminder_times = new { type = "array", description = "Minutes before dueTime to send reminders", items = new { type = "integer" } },
            slip_alert_enabled = new { type = "boolean", description = "Enable slip alert notifications" },
            is_flexible = new { type = "boolean", description = "True for flexible frequency" },
            due_date = new { type = "string", description = "YYYY-MM-DD override for due date" },
            scheduled_reminders = new
            {
                type = "array",
                description = "Absolute-time reminders for habits WITHOUT a due_time",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        when = new { type = "string", description = "day_before or same_day", @enum = new[] { "day_before", "same_day" } },
                        time = new { type = "string", description = "HH:mm 24h format" }
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

        FrequencyUnit? frequencyUnit = null;
        if (args.TryGetProperty("frequency_unit", out var fuEl) && fuEl.ValueKind == JsonValueKind.String)
        {
            if (Enum.TryParse<FrequencyUnit>(fuEl.GetString(), ignoreCase: true, out var fu))
                frequencyUnit = fu;
        }

        int? frequencyQuantity = null;
        if (args.TryGetProperty("frequency_quantity", out var fqEl) && fqEl.ValueKind == JsonValueKind.Number)
            frequencyQuantity = fqEl.GetInt32();
        frequencyQuantity ??= frequencyUnit is not null ? 1 : null;

        IReadOnlyList<DayOfWeek>? days = null;
        if (args.TryGetProperty("days", out var daysEl) && daysEl.ValueKind == JsonValueKind.Array)
        {
            var parsed = new List<DayOfWeek>();
            foreach (var d in daysEl.EnumerateArray())
            {
                var dayStr = d.GetString();
                if (dayStr is not null && Enum.TryParse<DayOfWeek>(dayStr, ignoreCase: true, out var day))
                    parsed.Add(day);
            }
            if (parsed.Count > 0) days = parsed;
        }

        TimeOnly? dueTime = null;
        if (args.TryGetProperty("due_time", out var dtEl) && dtEl.ValueKind == JsonValueKind.String)
        {
            if (TimeOnly.TryParse(dtEl.GetString(), out var time))
                dueTime = time;
        }

        TimeOnly? dueEndTime = null;
        if (args.TryGetProperty("due_end_time", out var detEl) && detEl.ValueKind == JsonValueKind.String)
        {
            if (TimeOnly.TryParse(detEl.GetString(), out var endTime))
                dueEndTime = endTime;
        }

        string? description = null;
        if (args.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
            description = descEl.GetString();

        bool isBadHabit = args.TryGetProperty("is_bad_habit", out var ibhEl) && ibhEl.ValueKind == JsonValueKind.True;
        bool reminderEnabled = args.TryGetProperty("reminder_enabled", out var reEl) && reEl.ValueKind == JsonValueKind.True;
        bool slipAlertEnabled = args.TryGetProperty("slip_alert_enabled", out var saEl) && saEl.ValueKind == JsonValueKind.True;
        bool isFlexible = args.TryGetProperty("is_flexible", out var ifEl) && ifEl.ValueKind == JsonValueKind.True;

        IReadOnlyList<int>? reminderTimes = null;
        if (args.TryGetProperty("reminder_times", out var rtEl) && rtEl.ValueKind == JsonValueKind.Array)
        {
            var parsed = new List<int>();
            foreach (var r in rtEl.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.Number)
                    parsed.Add(r.GetInt32());
            }
            if (parsed.Count > 0) reminderTimes = parsed;
        }

        DateOnly? dueDate = null;
        if (args.TryGetProperty("due_date", out var ddEl) && ddEl.ValueKind == JsonValueKind.String)
        {
            if (DateOnly.TryParseExact(ddEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd))
                dueDate = dd;
        }

        IReadOnlyList<ScheduledReminderTime>? scheduledReminders = null;
        if (args.TryGetProperty("scheduled_reminders", out var srEl) && srEl.ValueKind == JsonValueKind.Array)
        {
            var parsed = new List<ScheduledReminderTime>();
            foreach (var item in srEl.EnumerateArray())
            {
                string? when = null;
                string? timeStr = null;
                if (item.TryGetProperty("when", out var wEl) && wEl.ValueKind == JsonValueKind.String)
                    when = wEl.GetString();
                if (item.TryGetProperty("time", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                    timeStr = tEl.GetString();
                if (when is not ("day_before" or "same_day") || timeStr is null) continue;
                if (!TimeOnly.TryParse(timeStr, out var time)) continue;
                parsed.Add(new ScheduledReminderTime(when, time));
            }
            if (parsed.Count > 0) scheduledReminders = parsed;
        }

        var result = await mediator.Send(
            new Orbit.Application.Habits.Commands.CreateSubHabitCommand(
                userId,
                parentHabitId,
                title,
                description,
                frequencyUnit,
                frequencyQuantity,
                days,
                dueTime,
                dueEndTime,
                isBadHabit,
                reminderEnabled,
                reminderTimes,
                slipAlertEnabled,
                DueDate: dueDate,
                IsFlexible: isFlexible,
                ScheduledReminders: scheduledReminders), ct);

        if (result.IsFailure)
            return new ToolResult(false, Error: result.Error);

        return new ToolResult(true, EntityId: result.Value.ToString(), EntityName: title);
    }
}
