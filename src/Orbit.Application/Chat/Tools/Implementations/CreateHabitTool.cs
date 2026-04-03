using System.Text.Json;
using Orbit.Application.Chat.Tools;
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
        type = JsonSchemaTypes.Object,
        properties = new
        {
            title = new { type = JsonSchemaTypes.String, description = "Name of the habit" },
            description = new { type = JsonSchemaTypes.String, description = "Optional description" },
            frequency_unit = new
            {
                type = JsonSchemaTypes.String,
                description = "Recurrence unit. Omit for one-time tasks.",
                nullable = true,
                @enum = JsonSchemaTypes.FrequencyUnitEnum
            },
            frequency_quantity = new { type = JsonSchemaTypes.Integer, description = "How often (e.g. every 2 days). Defaults to 1." },
            days = new
            {
                type = JsonSchemaTypes.Array,
                description = "Specific weekdays (e.g. ['Monday','Wednesday','Friday']). Only when frequency_quantity is 1.",
                items = new { type = JsonSchemaTypes.String }
            },
            due_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. Defaults to today." },
            end_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. Optional end date. Habit stops appearing after this date. Only for recurring habits.", nullable = true },
            due_time = new { type = JsonSchemaTypes.String, description = "HH:mm 24h format" },
            is_bad_habit = new { type = JsonSchemaTypes.Boolean, description = "Whether this is a bad habit to reduce. Defaults to false." },
            is_flexible = new { type = JsonSchemaTypes.Boolean, description = "True for window-based tracking (e.g. '3x per week, any days'). Cannot have days set. Requires frequency_unit." },
            slip_alert_enabled = new { type = JsonSchemaTypes.Boolean, description = "Enable slip pattern alerts. Defaults to true for bad habits." },
            reminder_enabled = new { type = JsonSchemaTypes.Boolean, description = "Enable reminders" },
            reminder_times = new
            {
                type = JsonSchemaTypes.Array,
                description = "Minutes before due time to remind (e.g. [15, 60])",
                items = new { type = JsonSchemaTypes.Integer }
            },
            tag_names = new
            {
                type = JsonSchemaTypes.Array,
                description = "Tag names to assign. Existing tags reused, new ones created.",
                items = new { type = JsonSchemaTypes.String }
            },
            checklist_items = new
            {
                type = JsonSchemaTypes.Array,
                description = "Inline checklist items",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        text = new { type = JsonSchemaTypes.String, description = "Checklist item text" },
                        is_checked = new { type = JsonSchemaTypes.Boolean, description = "Whether checked. Defaults to false." }
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
                        time = new { type = JsonSchemaTypes.String, description = "HH:mm 24h format, e.g. '09:00'" }
                    },
                    required = new[] { "when", "time" }
                }
            },
            goal_ids = new
            {
                type = JsonSchemaTypes.Array,
                description = "IDs of goals to link this habit to",
                items = new { type = JsonSchemaTypes.String }
            },
            sub_habits = new
            {
                type = JsonSchemaTypes.Array,
                description = "Inline child habits to create under this parent",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        title = new { type = JsonSchemaTypes.String, description = "Sub-habit name" },
                        description = new { type = JsonSchemaTypes.String, description = "Optional description" },
                        frequency_unit = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.FrequencyUnitEnum },
                        frequency_quantity = new { type = JsonSchemaTypes.Integer },
                        days = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } },
                        due_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD" },
                        is_bad_habit = new { type = JsonSchemaTypes.Boolean },
                        checklist_items = new
                        {
                            type = JsonSchemaTypes.Array,
                            description = "Inline checklist items",
                            items = new
                            {
                                type = JsonSchemaTypes.Object,
                                properties = new
                                {
                                    text = new { type = JsonSchemaTypes.String, description = "Checklist item text" },
                                    is_checked = new { type = JsonSchemaTypes.Boolean, description = "Whether checked. Defaults to false." }
                                },
                                required = new[] { "text" }
                            }
                        }
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

        var habitGate = await payGate.CanCreateHabits(userId, 1, ct);
        if (habitGate.IsFailure)
            return new ToolResult(false, Error: habitGate.Error);

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        var habitResult = BuildParentHabit(args, userId, title, today);
        if (habitResult.IsFailure)
            return new ToolResult(false, Error: habitResult.Error);

        var habit = habitResult.Value;

        var subError = await CreateInlineSubHabitsAsync(args, habit, userId, today, ct);
        if (subError is not null)
            return subError;

        await habitRepository.AddAsync(habit, ct);
        await AssignTagsFromArgsAsync(args, habit, userId, ct);
        await LinkGoalsFromArgsAsync(args, habit, userId, ct);
        await unitOfWork.SaveChangesAsync(ct);

        return new ToolResult(true, EntityId: habit.Id.ToString(), EntityName: habit.Title);
    }

    private static Domain.Common.Result<Habit> BuildParentHabit(
        JsonElement args, Guid userId, string title, DateOnly today)
    {
        var frequencyUnit = JsonArgumentParser.ParseFrequencyUnit(args);
        int? frequencyQuantity = JsonArgumentParser.GetOptionalInt(args, "frequency_quantity")
            ?? (frequencyUnit is not null ? 1 : null);
        bool isBadHabit = JsonArgumentParser.GetOptionalBool(args, "is_bad_habit") ?? false;

        return Habit.Create(new HabitCreateParams(
            userId, title, frequencyUnit, frequencyQuantity,
            JsonArgumentParser.GetOptionalString(args, "description"),
            Days: JsonArgumentParser.ParseDays(args),
            IsBadHabit: isBadHabit,
            DueDate: JsonArgumentParser.ParseDateOnly(args, "due_date") ?? today,
            DueTime: JsonArgumentParser.ParseTimeOnly(args, "due_time"),
            ReminderEnabled: JsonArgumentParser.GetOptionalBool(args, "reminder_enabled") ?? false,
            ReminderTimes: JsonArgumentParser.ParseIntArray(args, "reminder_times"),
            SlipAlertEnabled: JsonArgumentParser.GetOptionalBool(args, "slip_alert_enabled") ?? isBadHabit,
            ChecklistItems: JsonArgumentParser.ParseChecklistItems(args),
            IsFlexible: JsonArgumentParser.GetOptionalBool(args, "is_flexible") ?? false,
            EndDate: JsonArgumentParser.ParseDateOnly(args, "end_date"),
            ScheduledReminders: JsonArgumentParser.ParseScheduledReminders(args)));
    }

    private async Task<ToolResult?> CreateInlineSubHabitsAsync(
        JsonElement args, Habit parent, Guid userId, DateOnly parentDueDate, CancellationToken ct)
    {
        if (!args.TryGetProperty("sub_habits", out var subEl) || subEl.ValueKind != JsonValueKind.Array)
            return null;

        var subGate = await payGate.CanCreateSubHabits(userId, ct);
        if (subGate.IsFailure)
            return new ToolResult(false, Error: subGate.Error);

        var parentFreqUnit = JsonArgumentParser.ParseFrequencyUnit(args);
        var parentFreqQty = JsonArgumentParser.GetOptionalInt(args, "frequency_quantity")
            ?? (parentFreqUnit is not null ? 1 : null);
        var parentDays = JsonArgumentParser.ParseDays(args);

        foreach (var sub in subEl.EnumerateArray())
        {
            var childResult = BuildChildHabit(sub, userId, parent.Id, parentFreqUnit, parentFreqQty, parentDays, parentDueDate);
            if (childResult.IsFailure)
                return new ToolResult(false, Error: childResult.Error);

            await habitRepository.AddAsync(childResult.Value, ct);
        }

        return null;
    }

    private static Domain.Common.Result<Habit> BuildChildHabit(
        JsonElement sub, Guid userId, Guid parentId,
        FrequencyUnit? parentFreqUnit, int? parentFreqQty,
        List<DayOfWeek>? parentDays, DateOnly parentDueDate)
    {
        var subDays = JsonArgumentParser.ParseDays(sub);
        if (subDays is null || subDays.Count == 0)
            subDays = parentDays;

        return Habit.Create(new HabitCreateParams(
            userId,
            JsonArgumentParser.GetOptionalString(sub, "title") ?? "Untitled",
            JsonArgumentParser.ParseFrequencyUnit(sub) ?? parentFreqUnit,
            JsonArgumentParser.GetOptionalInt(sub, "frequency_quantity") ?? parentFreqQty,
            JsonArgumentParser.GetOptionalString(sub, "description"),
            Days: subDays,
            IsBadHabit: JsonArgumentParser.GetOptionalBool(sub, "is_bad_habit") ?? false,
            DueDate: JsonArgumentParser.ParseDateOnly(sub, "due_date") ?? parentDueDate,
            ParentHabitId: parentId,
            ChecklistItems: JsonArgumentParser.ParseChecklistItems(sub)));
    }

    private async Task AssignTagsFromArgsAsync(JsonElement args, Habit habit, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("tag_names", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Array)
            return;

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

    private async Task LinkGoalsFromArgsAsync(JsonElement args, Habit habit, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty("goal_ids", out var goalIdsEl) || goalIdsEl.ValueKind != JsonValueKind.Array)
            return;

        var goalIds = new List<Guid>();
        foreach (var g in goalIdsEl.EnumerateArray())
        {
            if (Guid.TryParse(g.GetString(), out var gid))
                goalIds.Add(gid);
        }

        if (goalIds.Count == 0)
            return;

        var goals = await goalRepository.FindTrackedAsync(
            gl => goalIds.Contains(gl.Id) && gl.UserId == userId, ct);

        foreach (var goal in goals)
            habit.AddGoal(goal);
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
}
