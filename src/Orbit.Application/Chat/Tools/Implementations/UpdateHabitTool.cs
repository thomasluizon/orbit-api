using System.Text.Json;
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
        type = "OBJECT",
        properties = new
        {
            habit_id = new { type = "STRING", description = "ID of the habit to update" },
            title = new { type = "STRING", description = "New title" },
            description = new { type = "STRING", description = "New description", nullable = true },
            frequency_unit = new
            {
                type = "STRING",
                description = "New recurrence unit. Set to null to convert to one-time task.",
                nullable = true,
                @enum = new[] { "Day", "Week", "Month", "Year" }
            },
            frequency_quantity = new { type = "INTEGER", description = "New frequency quantity" },
            days = new
            {
                type = "ARRAY",
                description = "New weekday schedule",
                items = new { type = "STRING" }
            },
            due_date = new { type = "STRING", description = "New due date (YYYY-MM-DD)" },
            end_date = new { type = "STRING", description = "New end date (YYYY-MM-DD). Set to null to clear. Habit stops after this date.", nullable = true },
            due_time = new { type = "STRING", description = "New due time (HH:mm). Set to null to clear.", nullable = true },
            is_bad_habit = new { type = "BOOLEAN", description = "Whether this is a bad habit" },
            is_flexible = new { type = "BOOLEAN", description = "True for window-based tracking. Cannot have days set." },
            reminder_enabled = new { type = "BOOLEAN", description = "Enable or disable reminders" },
            reminder_times = new
            {
                type = "ARRAY",
                description = "New reminder times (minutes before)",
                items = new { type = "INTEGER" }
            },
            checklist_items = new
            {
                type = "ARRAY",
                description = "Replace checklist items",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        text = new { type = "STRING", description = "Checklist item text" },
                        is_checked = new { type = "BOOLEAN", description = "Whether checked" }
                    },
                    required = new[] { "text" }
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

        // Resolve each field: absent = keep existing, null = clear, value = update
        var title = PropertyExists(args, "title")
            ? GetString(args, "title") ?? habit.Title
            : habit.Title;

        var description = PropertyExists(args, "description")
            ? GetString(args, "description")
            : habit.Description;

        FrequencyUnit? frequencyUnit;
        if (PropertyExists(args, "frequency_unit"))
        {
            var fuStr = GetString(args, "frequency_unit");
            frequencyUnit = fuStr is not null && Enum.TryParse<FrequencyUnit>(fuStr, ignoreCase: true, out var fu)
                ? fu
                : null; // explicit null = make one-time
        }
        else
        {
            frequencyUnit = habit.FrequencyUnit;
        }

        var frequencyQuantity = PropertyExists(args, "frequency_quantity")
            ? GetInt(args, "frequency_quantity") ?? habit.FrequencyQuantity
            : habit.FrequencyQuantity;

        IReadOnlyList<DayOfWeek>? days;
        if (PropertyExists(args, "days"))
        {
            days = ParseDays(args);
        }
        else
        {
            days = habit.Days.ToList();
        }

        bool isBadHabit = PropertyExists(args, "is_bad_habit")
            ? GetBool(args, "is_bad_habit") ?? habit.IsBadHabit
            : habit.IsBadHabit;

        DateOnly? dueDate = PropertyExists(args, "due_date")
            ? ParseDateOnly(args, "due_date") ?? habit.DueDate
            : habit.DueDate;

        // due_time: absent = keep, null = clear, value = set
        TimeOnly? dueTime;
        if (PropertyExists(args, "due_time"))
        {
            var dtStr = GetString(args, "due_time");
            dueTime = dtStr is not null ? ParseTimeOnly(dtStr) : null;
        }
        else
        {
            dueTime = habit.DueTime;
        }

        bool? reminderEnabled = PropertyExists(args, "reminder_enabled")
            ? GetBool(args, "reminder_enabled")
            : null;

        IReadOnlyList<int>? reminderTimes = PropertyExists(args, "reminder_times")
            ? ParseIntArray(args, "reminder_times")
            : null;

        IReadOnlyList<ChecklistItem>? checklistItems = PropertyExists(args, "checklist_items")
            ? ParseChecklistItems(args)
            : null;

        bool? isFlexible = PropertyExists(args, "is_flexible")
            ? GetBool(args, "is_flexible")
            : null;

        // end_date: absent = keep, null = clear, value = set
        DateOnly? endDate = null;
        bool? clearEndDate = null;
        if (PropertyExists(args, "end_date"))
        {
            var edStr = GetString(args, "end_date");
            if (edStr is null)
                clearEndDate = true;
            else
                endDate = DateOnly.TryParseExact(edStr, "yyyy-MM-dd", out var ed) ? ed : null;
        }

        var result = habit.Update(
            title,
            description,
            frequencyUnit,
            frequencyQuantity,
            days,
            isBadHabit,
            dueDate,
            dueTime: dueTime,
            reminderEnabled: reminderEnabled,
            reminderTimes: reminderTimes,
            checklistItems: checklistItems,
            isFlexible: isFlexible,
            endDate: endDate,
            clearEndDate: clearEndDate);

        if (result.IsFailure)
            return new ToolResult(false, Error: result.Error);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }

    private static bool PropertyExists(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out _);

    private static string? GetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.String) return val.GetString();
            if (val.ValueKind == JsonValueKind.Null) return null;
        }
        return null;
    }

    private static int? GetInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return null;
    }

    private static bool? GetBool(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    private static IReadOnlyList<DayOfWeek>? ParseDays(JsonElement el)
    {
        if (!el.TryGetProperty("days", out var daysEl) || daysEl.ValueKind != JsonValueKind.Array)
            return null;

        var days = new List<DayOfWeek>();
        foreach (var d in daysEl.EnumerateArray())
        {
            var dayStr = d.GetString();
            if (dayStr is not null && Enum.TryParse<DayOfWeek>(dayStr, ignoreCase: true, out var day))
                days.Add(day);
        }
        return days;
    }

    private static DateOnly? ParseDateOnly(JsonElement el, string prop)
    {
        var str = GetString(el, prop);
        if (str is null) return null;
        return DateOnly.TryParse(str, out var date) ? date : null;
    }

    private static TimeOnly? ParseTimeOnly(string str) =>
        TimeOnly.TryParse(str, out var time) ? time : null;

    private static IReadOnlyList<int>? ParseIntArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<int>();
        foreach (var item in arrEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number)
                items.Add(item.GetInt32());
        }
        return items.Count > 0 ? items : null;
    }

    private static IReadOnlyList<ChecklistItem>? ParseChecklistItems(JsonElement el)
    {
        if (!el.TryGetProperty("checklist_items", out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<ChecklistItem>();
        foreach (var item in arrEl.EnumerateArray())
        {
            var text = GetString(item, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var isChecked = GetBool(item, "is_checked") ?? false;
            items.Add(new ChecklistItem(text, isChecked));
        }
        return items.Count > 0 ? items : null;
    }
}
