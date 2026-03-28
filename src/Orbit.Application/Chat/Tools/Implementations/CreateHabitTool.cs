using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

public class CreateHabitTool(
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<Tag> tagRepository,
    IGenericRepository<Goal> goalRepository,
    IUserDateService userDateService,
    IPayGateService payGate,
    IUnitOfWork unitOfWork) : IAiTool
{
    public string Name => "create_habit";

    public string Description =>
        "Create a new habit or one-time task. For recurring habits, set frequency_unit and optionally days. For one-time tasks, omit frequency_unit. When user says 'X times per week' without specifying days, set is_flexible=true, frequency_unit='Week', frequency_quantity=X. When user specifies exact days, use frequency_unit='Day', frequency_quantity=1, days=[specified days]. Example: '3x per week' (no days) = flexible Week/3. '3x per week on Mon/Wed/Fri' = Day/1/[Mon,Wed,Fri].";

    public object GetParameterSchema() => new
    {
        type = "OBJECT",
        properties = new
        {
            title = new { type = "STRING", description = "Name of the habit" },
            description = new { type = "STRING", description = "Optional description" },
            frequency_unit = new
            {
                type = "STRING",
                description = "Recurrence unit. Omit for one-time tasks.",
                nullable = true,
                @enum = new[] { "Day", "Week", "Month", "Year" }
            },
            frequency_quantity = new { type = "INTEGER", description = "How often (e.g. every 2 days). Defaults to 1." },
            days = new
            {
                type = "ARRAY",
                description = "Specific weekdays (e.g. ['Monday','Wednesday','Friday']). Only when frequency_quantity is 1.",
                items = new { type = "STRING" }
            },
            due_date = new { type = "STRING", description = "YYYY-MM-DD. Defaults to today." },
            end_date = new { type = "STRING", description = "YYYY-MM-DD. Optional end date. Habit stops appearing after this date. Only for recurring habits.", nullable = true },
            due_time = new { type = "STRING", description = "HH:mm 24h format" },
            is_bad_habit = new { type = "BOOLEAN", description = "Whether this is a bad habit to reduce. Defaults to false." },
            is_flexible = new { type = "BOOLEAN", description = "True for window-based tracking (e.g. '3x per week, any days'). Cannot have days set. Requires frequency_unit." },
            slip_alert_enabled = new { type = "BOOLEAN", description = "Enable slip pattern alerts. Defaults to true for bad habits." },
            reminder_enabled = new { type = "BOOLEAN", description = "Enable reminders" },
            reminder_times = new
            {
                type = "ARRAY",
                description = "Minutes before due time to remind (e.g. [15, 60])",
                items = new { type = "INTEGER" }
            },
            tag_names = new
            {
                type = "ARRAY",
                description = "Tag names to assign. Existing tags reused, new ones created.",
                items = new { type = "STRING" }
            },
            checklist_items = new
            {
                type = "ARRAY",
                description = "Inline checklist items",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        text = new { type = "STRING", description = "Checklist item text" },
                        is_checked = new { type = "BOOLEAN", description = "Whether checked. Defaults to false." }
                    },
                    required = new[] { "text" }
                }
            },
            goal_ids = new
            {
                type = "ARRAY",
                description = "IDs of goals to link this habit to",
                items = new { type = "STRING" }
            },
            sub_habits = new
            {
                type = "ARRAY",
                description = "Inline child habits to create under this parent",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        title = new { type = "STRING", description = "Sub-habit name" },
                        description = new { type = "STRING", description = "Optional description" },
                        frequency_unit = new { type = "STRING", @enum = new[] { "Day", "Week", "Month", "Year" } },
                        frequency_quantity = new { type = "INTEGER" },
                        days = new { type = "ARRAY", items = new { type = "STRING" } },
                        due_date = new { type = "STRING", description = "YYYY-MM-DD" },
                        is_bad_habit = new { type = "BOOLEAN" }
                    },
                    required = new[] { "title" }
                }
            }
        },
        required = new[] { "title" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("title", out var titleEl) || string.IsNullOrWhiteSpace(titleEl.GetString()))
            return new ToolResult(false, Error: "title is required.");

        var title = titleEl.GetString() ?? string.Empty;

        // Check habit limit
        var habitGate = await payGate.CanCreateHabits(userId, 1, ct);
        if (habitGate.IsFailure)
            return new ToolResult(false, Error: habitGate.Error);

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        // Parse optional fields
        string? description = GetOptionalString(args, "description");
        FrequencyUnit? frequencyUnit = ParseFrequencyUnit(args);
        int? frequencyQuantity = GetOptionalInt(args, "frequency_quantity") ?? (frequencyUnit is not null ? 1 : null);
        DateOnly dueDate = ParseDateOnly(args, "due_date") ?? today;
        TimeOnly? dueTime = ParseTimeOnly(args, "due_time");
        bool isBadHabit = GetOptionalBool(args, "is_bad_habit") ?? false;
        bool isFlexible = GetOptionalBool(args, "is_flexible") ?? false;
        bool slipAlertEnabled = GetOptionalBool(args, "slip_alert_enabled") ?? isBadHabit;
        bool reminderEnabled = GetOptionalBool(args, "reminder_enabled") ?? false;
        var reminderTimes = ParseIntArray(args, "reminder_times");
        var days = ParseDays(args);
        var checklistItems = ParseChecklistItems(args);
        DateOnly? endDate = ParseDateOnly(args, "end_date");

        var habitResult = Habit.Create(
            userId,
            title,
            frequencyUnit,
            frequencyQuantity,
            description,
            days: days,
            isBadHabit: isBadHabit,
            dueDate: dueDate,
            dueTime: dueTime,
            reminderEnabled: reminderEnabled,
            reminderTimes: reminderTimes,
            slipAlertEnabled: slipAlertEnabled,
            checklistItems: checklistItems,
            isFlexible: isFlexible,
            endDate: endDate);

        if (habitResult.IsFailure)
            return new ToolResult(false, Error: habitResult.Error);

        var habit = habitResult.Value;

        // Handle inline sub-habits
        if (args.TryGetProperty("sub_habits", out var subEl) && subEl.ValueKind == JsonValueKind.Array)
        {
            var subGate = await payGate.CanCreateSubHabits(userId, ct);
            if (subGate.IsFailure)
                return new ToolResult(false, Error: subGate.Error);

            foreach (var sub in subEl.EnumerateArray())
            {
                var subTitle = GetOptionalString(sub, "title") ?? "Untitled";
                var subFreqUnit = ParseFrequencyUnit(sub) ?? frequencyUnit;
                var subFreqQty = GetOptionalInt(sub, "frequency_quantity") ?? frequencyQuantity;
                var subDays = ParseDays(sub);
                if (subDays is null || subDays.Count == 0)
                    subDays = days;
                var subDueDate = ParseDateOnly(sub, "due_date") ?? dueDate;
                bool subIsBadHabit = GetOptionalBool(sub, "is_bad_habit") ?? false;

                var childResult = Habit.Create(
                    userId,
                    subTitle,
                    subFreqUnit,
                    subFreqQty,
                    GetOptionalString(sub, "description"),
                    days: subDays,
                    isBadHabit: subIsBadHabit,
                    dueDate: subDueDate,
                    parentHabitId: habit.Id);

                if (childResult.IsFailure)
                    return new ToolResult(false, Error: childResult.Error);

                await habitRepository.AddAsync(childResult.Value, ct);
            }
        }

        await habitRepository.AddAsync(habit, ct);

        // Assign tags
        if (args.TryGetProperty("tag_names", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            var tagNames = new List<string>();
            foreach (var t in tagsEl.EnumerateArray())
            {
                var name = t.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    tagNames.Add(name);
            }

            if (tagNames.Count > 0)
                await AssignTagsToHabitAsync(habit, tagNames, userId, ct);
        }

        // Link goals
        if (args.TryGetProperty("goal_ids", out var goalIdsEl) && goalIdsEl.ValueKind == JsonValueKind.Array)
        {
            var goalIds = new List<Guid>();
            foreach (var g in goalIdsEl.EnumerateArray())
            {
                if (Guid.TryParse(g.GetString(), out var gid))
                    goalIds.Add(gid);
            }

            if (goalIds.Count > 0)
            {
                var goals = await goalRepository.FindTrackedAsync(
                    gl => goalIds.Contains(gl.Id) && gl.UserId == userId,
                    ct);

                foreach (var goal in goals)
                    habit.AddGoal(goal);
            }
        }

        // Save immediately so subsequent tool calls (e.g. create_sub_habit) can find this habit
        await unitOfWork.SaveChangesAsync(ct);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }

    private async Task AssignTagsToHabitAsync(Habit habit, List<string> tagNames, Guid userId, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in tagNames)
        {
            var capitalized = Capitalize(name.Trim());
            if (string.IsNullOrEmpty(capitalized) || !seen.Add(capitalized)) continue;

            var existing = await tagRepository.FindOneTrackedAsync(
                t => t.UserId == userId && t.Name == capitalized, cancellationToken: ct);

            if (existing is not null)
            {
                habit.AddTag(existing);
            }
            else
            {
                var createResult = Tag.Create(userId, capitalized, "#7c3aed");
                if (createResult.IsSuccess)
                {
                    await tagRepository.AddAsync(createResult.Value, ct);
                    habit.AddTag(createResult.Value);
                }
            }
        }
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..].ToLower();

    private static string? GetOptionalString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            return val.GetString();
        return null;
    }

    private static int? GetOptionalInt(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            return val.GetInt32();
        return null;
    }

    private static bool? GetOptionalBool(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    private static FrequencyUnit? ParseFrequencyUnit(JsonElement el)
    {
        var str = GetOptionalString(el, "frequency_unit");
        if (str is null) return null;
        return Enum.TryParse<FrequencyUnit>(str, ignoreCase: true, out var unit) ? unit : null;
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
        return days.Count > 0 ? days : null;
    }

    private static DateOnly? ParseDateOnly(JsonElement el, string prop)
    {
        var str = GetOptionalString(el, prop);
        if (str is null) return null;
        return DateOnly.TryParseExact(str, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static TimeOnly? ParseTimeOnly(JsonElement el, string prop)
    {
        var str = GetOptionalString(el, prop);
        if (str is null) return null;
        return TimeOnly.TryParse(str, out var time) ? time : null;
    }

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
            var text = GetOptionalString(item, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var isChecked = GetOptionalBool(item, "is_checked") ?? false;
            items.Add(new ChecklistItem(text, isChecked));
        }
        return items.Count > 0 ? items : null;
    }
}
