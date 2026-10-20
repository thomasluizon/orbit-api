using System.Text.Json;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
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
    IUnitOfWork unitOfWork) : IAiTool, IClarificationPrecheckTool, IArgumentCheckTool
{
    private const string TitleProperty = "title";

    public string Name => "create_habit";

    public int Order => 0;

    public string Description =>
        "Create a new habit or one-time task in one call: pass tag_names, sub_habits, goal_ids, and checklist_items inline rather than following up with assign_tags, create_sub_habit, or link_*_to_* calls. A single event on a named calendar date is a one-time task: set due_date, set frequency_unit=null, and omit frequency_quantity, interval_weeks, and days, even if a weekday is mentioned. For recurring habits, set frequency_unit and optionally days; due_date then sets the recurrence start date. In mixed requests, create recurring classes with recurrence and dated extra classes as separate one-time tasks; dated sub_habits inherit the parent frequency but not its weekdays; set frequency_unit=null explicitly for a one-time child. For other explicit one-time tasks (e.g., 'just once', 'this Friday only', 'uma vez'), set frequency_unit=null. If the user called it a habit/rotina/hábito or did not state a frequency, ask first via a NeedsClarification card instead of guessing. Structure: checklist_items for atomic sub-steps done together (shopping/prep/packing lists); sub_habits for sub-activities needing independent tracking, streaks, or schedules; never put list items only in the description. Frequency: 'X times per week' without specific days = is_flexible=true, frequency_unit='Week', frequency_quantity=X; recurring weekdays = frequency_unit='Day', frequency_quantity=1, days=[specified weekdays]. Week with quantity 1 and days is also accepted and normalized to Day. Preserve the requested weekdays exactly. For 'before 15h' without a fixed start, set due_end_time='15:00' and omit due_time; for a time range, set both.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            title = new { type = JsonSchemaTypes.String, description = "Name of the habit" },
            description = new { type = JsonSchemaTypes.String, description = "Optional description" },
            emoji = new { type = JsonSchemaTypes.String, description = "Emoji used as the habit icon. Pick a relevant emoji when clear, or use the exact emoji requested by the user.", nullable = true },
            frequency_unit = new
            {
                type = JsonSchemaTypes.String,
                description = "Recurrence unit. Omit or set null for one-time tasks, including events on a single named date.",
                nullable = true,
                @enum = JsonSchemaTypes.FrequencyUnitEnum
            },
            frequency_quantity = new { type = JsonSchemaTypes.Integer, description = "How often (e.g. every 2 days). Defaults to 1." },
            interval_weeks = new { type = JsonSchemaTypes.Integer, description = "Repeat interval in weeks. Omit for every week. Minimum 1." },
            days = new
            {
                type = JsonSchemaTypes.Array,
                description = "Recurring weekdays (e.g. ['Monday','Wednesday','Friday']). Requires frequency_unit='Day' and frequency_quantity=1; Week with quantity 1 is normalized to Day. Omit for dated one-time tasks.",
                items = new { type = JsonSchemaTypes.String }
            },
            due_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. For a single dated event, set frequency_unit=null and omit days. For recurring habits, this is the start date. Defaults to today." },
            end_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. Optional end date. Habit stops appearing after this date. Only for recurring habits.", nullable = true },
            due_time = new { type = JsonSchemaTypes.String, description = "HH:mm 24h format start time. Omit when there is only a deadline.", nullable = true },
            due_end_time = new { type = JsonSchemaTypes.String, description = "HH:mm 24h format end time or deadline. For 'before 15h' without a fixed start, set 15:00 and omit due_time.", nullable = true },
            is_general = new { type = JsonSchemaTypes.Boolean, description = "General habit without a schedule. Children inherit this value." },
            relative_reminders = BulkHabitToolArguments.RelativeRemindersSchema(),
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
                description = "Atomic sub-steps done together in one execution of this habit. Use for shopping lists, ingredient lists, prep steps, packing lists. NOT for independently tracked activities - use sub_habits for those.",
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
                description = "Independently trackable child habits under this parent. Use when each sub-activity has its own schedule, streak, or time of day. NOT for atomic prep steps done together - use checklist_items on the parent for those.",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        title = new { type = JsonSchemaTypes.String, description = "Sub-habit name" },
                        description = new { type = JsonSchemaTypes.String, description = "Optional description" },
                        emoji = new { type = JsonSchemaTypes.String, description = "Emoji used as the sub-habit icon. Pick a relevant emoji when clear.", nullable = true },
                        frequency_unit = new { type = JsonSchemaTypes.String, description = "Override parent recurrence. Set null explicitly for a one-time task. Otherwise the parent's unit and quantity are inherited.", nullable = true, @enum = JsonSchemaTypes.FrequencyUnitEnum },
                        frequency_quantity = new { type = JsonSchemaTypes.Integer },
                        interval_weeks = new { type = JsonSchemaTypes.Integer, description = "Repeat interval in weeks. Minimum 1." },
                        days = new { type = JsonSchemaTypes.Array, description = "Recurring weekdays with Day (or Week normalized to Day) and quantity 1. Omit for dated one-time tasks.", items = new { type = JsonSchemaTypes.String } },
                        due_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. Sets the child's start date without inheriting the parent's weekdays. Set frequency_unit=null explicitly for a one-time task." },
                        due_time = new { type = JsonSchemaTypes.String, description = "Start time (HH:mm)", nullable = true },
                        due_end_time = new { type = JsonSchemaTypes.String, description = "End time or deadline (HH:mm)", nullable = true },
                        end_date = new { type = JsonSchemaTypes.String, description = "End date (YYYY-MM-DD)", nullable = true },
                        reminder_enabled = new { type = JsonSchemaTypes.Boolean },
                        reminder_times = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.Integer } },
                        slip_alert_enabled = new { type = JsonSchemaTypes.Boolean },
                        is_flexible = new { type = JsonSchemaTypes.Boolean },
                        tag_names = new { type = JsonSchemaTypes.Array, description = "Tag names for this child", items = new { type = JsonSchemaTypes.String } },
                        relative_reminders = BulkHabitToolArguments.RelativeRemindersSchema(),
                        scheduled_reminders = new
                        {
                            type = JsonSchemaTypes.Array,
                            items = new
                            {
                                type = JsonSchemaTypes.Object,
                                properties = new
                                {
                                    when = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.ScheduledReminderWhenEnum },
                                    time = new { type = JsonSchemaTypes.String }
                                },
                                required = new[] { "when", "time" }
                            }
                        },
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
                    required = new[] { TitleProperty }
                }
            }
        },
        required = new[] { TitleProperty }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty(TitleProperty, out var titleEl) || string.IsNullOrWhiteSpace(titleEl.GetString()))
            return new ToolResult(false, Error: "title is required.");

        var title = titleEl.GetString() ?? string.Empty;
        var fieldError = BulkHabitToolArguments.CheckCreateHabitFields(args);
        if (fieldError is null && args.TryGetProperty("sub_habits", out var subFields) && subFields.ValueKind == JsonValueKind.Array)
            fieldError = subFields.EnumerateArray().Select(BulkHabitToolArguments.CheckHabitFields).FirstOrDefault(error => error is not null);
        if (fieldError is not null)
            return new ToolResult(false, Error: fieldError);

        return await HabitCeilingLock.ExecuteAsync(
            unitOfWork,
            userId,
            transactionToken => ExecuteLockedAsync(args, userId, title, transactionToken),
            ct);
    }

    public async Task<Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        if (!args.TryGetProperty(TitleProperty, out var titleEl) || titleEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(titleEl.GetString()))
            return Domain.Common.Result.Failure("title is required.");

        var subHabits = args.TryGetProperty("sub_habits", out var subEl) && subEl.ValueKind == JsonValueKind.Array
            ? subEl.EnumerateArray().ToList()
            : [];
        var fieldError = BulkHabitToolArguments.CheckCreateHabitFields(args)
            ?? subHabits.Select(BulkHabitToolArguments.CheckHabitFields).FirstOrDefault(error => error is not null);
        if (fieldError is not null)
            return Domain.Common.Result.Failure(fieldError);

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var parent = BuildParentHabit(args, userId, titleEl.GetString()!, today);
        if (parent.IsFailure)
            return parent;

        var children = BuildChildHabits(subHabits, userId, parent.Value, today);
        return children.IsFailure ? children : Domain.Common.Result.Success();
    }

    private async Task<ToolResult> ExecuteLockedAsync(
        JsonElement args,
        Guid userId,
        string title,
        CancellationToken ct)
    {
        var habitGate = await payGate.CanCreateHabits(userId, 1, ct);
        if (habitGate.IsFailure)
            return ToolResult.FromFailure(habitGate);

        if (NeedsClarification(args))
        {
            return new ToolResult(true, EntityName: title, Payload: BuildFrequencyClarification(args));
        }

        var today = await userDateService.GetUserTodayAsync(userId, ct);

        var habitResult = BuildParentHabit(args, userId, title, today);
        if (habitResult.IsFailure)
            return ToolResult.FromFailure(habitResult);

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
        var dueDate = JsonArgumentParser.ParseDateOnly(args, "due_date");
        var days = JsonArgumentParser.ParseDays(args);
        var intervalWeeks = JsonArgumentParser.GetOptionalInt(args, "interval_weeks");
        var isFlexible = JsonArgumentParser.GetOptionalBool(args, "is_flexible") ?? false;
        if (dueDate is not null && frequencyUnit is null)
        {
            frequencyQuantity = null;
            days = null;
            intervalWeeks = null;
        }
        frequencyUnit = NormalizeWeekdayUnit(frequencyUnit, frequencyQuantity, days, isFlexible);
        bool isBadHabit = JsonArgumentParser.GetOptionalBool(args, "is_bad_habit") ?? false;

        var dueTime = JsonArgumentParser.ParseTimeOnly(args, "due_time");
        var reminderTimes = JsonArgumentParser.ParseIntArray(args, "reminder_times");
        var scheduledReminders = JsonArgumentParser.ParseScheduledReminders(args);

        return Habit.Create(new HabitCreateParams(
            userId, title, frequencyUnit, frequencyQuantity,
            dueDate ?? today,
            JsonArgumentParser.GetOptionalString(args, "description"),
            Days: days,
            IsBadHabit: isBadHabit,
            DueTime: dueTime,
            DueEndTime: JsonArgumentParser.ParseTimeOnly(args, "due_end_time"),
            ReminderEnabled: JsonArgumentParser.GetOptionalBool(args, "reminder_enabled") ?? false,
            ReminderTimes: reminderTimes,
            SlipAlertEnabled: JsonArgumentParser.GetOptionalBool(args, "slip_alert_enabled") ?? isBadHabit,
            ChecklistItems: JsonArgumentParser.ParseChecklistItems(args),
            IsFlexible: isFlexible,
            EndDate: JsonArgumentParser.ParseDateOnly(args, "end_date"),
            ScheduledReminders: scheduledReminders,
            Emoji: JsonArgumentParser.GetOptionalString(args, "emoji"),
            IntervalWeeks: intervalWeeks,
            IsGeneral: JsonArgumentParser.GetOptionalBool(args, "is_general") ?? false,
            RelativeReminders: JsonArgumentParser.ParseRelativeReminders(args)));
    }

    private async Task<ToolResult?> CreateInlineSubHabitsAsync(
        JsonElement args, Habit parent, Guid userId, DateOnly parentDueDate, CancellationToken ct)
    {
        if (!args.TryGetProperty("sub_habits", out var subEl) || subEl.ValueKind != JsonValueKind.Array)
            return null;

        var subGate = await payGate.CanCreateSubHabits(userId, ct);
        if (subGate.IsFailure)
            return ToolResult.FromFailure(subGate);

        var children = BuildChildHabits(subEl.EnumerateArray().ToList(), userId, parent, parentDueDate);
        if (children.IsFailure)
            return ToolResult.FromFailure(children);

        var childArguments = subEl.EnumerateArray().ToList();
        for (var index = 0; index < children.Value.Count; index++)
        {
            var child = children.Value[index];
            await habitRepository.AddAsync(child, ct);
            await AssignTagsFromArgsAsync(childArguments[index], child, userId, ct);
        }

        return null;
    }

    private static Domain.Common.Result<List<Habit>> BuildChildHabits(
        IReadOnlyList<JsonElement> subHabits, Guid userId, Habit parent, DateOnly parentDueDate)
    {
        var children = new List<Habit>();
        foreach (var sub in subHabits)
        {
            var childResult = BuildChildHabit(
                sub,
                userId,
                parent.Id,
                parent.FrequencyUnit,
                parent.FrequencyQuantity,
                parent.Days.ToList(),
                parentDueDate,
                parent.IntervalWeeks, parent.IsGeneral);
            if (childResult.IsFailure)
                return childResult.PropagateError<List<Habit>>();

            children.Add(childResult.Value);
        }

        return Domain.Common.Result.Success(children);
    }

    private static Domain.Common.Result<Habit> BuildChildHabit(
        JsonElement sub, Guid userId, Guid parentId,
        FrequencyUnit? parentFreqUnit, int? parentFreqQty,
        List<DayOfWeek>? parentDays, DateOnly parentDueDate, int? parentIntervalWeeks, bool parentIsGeneral)
    {
        var subFrequencyUnit = JsonArgumentParser.ParseFrequencyUnit(sub);
        var subFrequencyQuantity = JsonArgumentParser.GetOptionalInt(sub, "frequency_quantity");
        var subIntervalWeeks = JsonArgumentParser.GetOptionalInt(sub, "interval_weeks");
        var subDays = JsonArgumentParser.ParseDays(sub);
        var subDueDate = JsonArgumentParser.ParseDateOnly(sub, "due_date");
        var isOneTime = sub.TryGetProperty("frequency_unit", out var unit) && unit.ValueKind == JsonValueKind.Null;
        var hasExplicitCadence = isOneTime || subFrequencyUnit is not null
            || subFrequencyQuantity is not null
            || subIntervalWeeks is not null
            || subDays is not null;
        var frequencyUnit = isOneTime ? null : subFrequencyUnit ?? parentFreqUnit;
        var frequencyQuantity = isOneTime ? null : subFrequencyQuantity ?? (subFrequencyUnit is not null ? 1 : parentFreqQty);
        if (isOneTime)
            subDays = null;
        var isFlexible = JsonArgumentParser.GetOptionalBool(sub, "is_flexible") ?? false;
        frequencyUnit = NormalizeWeekdayUnit(frequencyUnit, frequencyQuantity, subDays, isFlexible);
        if (subDays is null && subDueDate is null && frequencyUnit == FrequencyUnit.Day && frequencyQuantity == 1)
            subDays = parentDays;

        return Habit.Create(new HabitCreateParams(
            userId,
            JsonArgumentParser.GetOptionalString(sub, TitleProperty) ?? "Untitled",
            frequencyUnit,
            frequencyQuantity,
            subDueDate ?? parentDueDate,
            JsonArgumentParser.GetOptionalString(sub, "description"),
            Days: subDays,
            IsBadHabit: JsonArgumentParser.GetOptionalBool(sub, "is_bad_habit") ?? false,
            ParentHabitId: parentId,
            DueTime: JsonArgumentParser.ParseTimeOnly(sub, "due_time"),
            DueEndTime: JsonArgumentParser.ParseTimeOnly(sub, "due_end_time"),
            ReminderEnabled: JsonArgumentParser.GetOptionalBool(sub, "reminder_enabled") ?? false,
            ReminderTimes: JsonArgumentParser.ParseIntArray(sub, "reminder_times"),
            SlipAlertEnabled: JsonArgumentParser.GetOptionalBool(sub, "slip_alert_enabled") ?? false,
            IsFlexible: isFlexible,
            IsGeneral: parentIsGeneral,
            EndDate: JsonArgumentParser.ParseDateOnly(sub, "end_date"),
            ScheduledReminders: JsonArgumentParser.ParseScheduledReminders(sub),
            RelativeReminders: JsonArgumentParser.ParseRelativeReminders(sub),
            Emoji: JsonArgumentParser.GetOptionalString(sub, "emoji"),
            ChecklistItems: JsonArgumentParser.ParseChecklistItems(sub),
            IntervalWeeks: isOneTime ? null : subIntervalWeeks ?? (hasExplicitCadence ? null : parentIntervalWeeks)));
    }

    private static FrequencyUnit? NormalizeWeekdayUnit(
        FrequencyUnit? unit, int? quantity, IReadOnlyList<DayOfWeek>? days, bool isFlexible = false) =>
        !isFlexible && unit == FrequencyUnit.Week && quantity == 1 && days is { Count: > 0 }
            ? FrequencyUnit.Day
            : unit;

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
        var tags = await HabitToolHelpers.ResolveOrCreateTagsAsync(tagRepository, tagNames, userId, ct);
        foreach (var tag in tags)
            habit.AddTag(tag);
    }

    private static readonly System.Text.RegularExpressions.Regex HabitWordRegex =
        new(
            @"\bhabit\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

    public bool NeedsClarification(JsonElement args)
    {
        return !args.TryGetProperty("frequency_unit", out _)
            && args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(TitleProperty, out var titleElement)
            && titleElement.ValueKind == JsonValueKind.String
            && IsHabitFlavoredTitle(titleElement.GetString() ?? string.Empty);
    }

    private static bool IsHabitFlavoredTitle(string title)
    {
        return HabitWordRegex.IsMatch(title)
            || title.Contains("rotina", StringComparison.OrdinalIgnoreCase)
            || title.Contains("hábito", StringComparison.OrdinalIgnoreCase);
    }

    private static NeedsClarificationPayload BuildFrequencyClarification(JsonElement args)
    {
        var dueDate = JsonArgumentParser.GetOptionalString(args, "due_date");
        var oneTimePatch = dueDate is null
            ? """{"frequency_unit":null}"""
            : JsonSerializer.Serialize(new { frequency_unit = (string?)null, due_date = dueDate });
        return new NeedsClarificationPayload(
            Question: "habits.clarification.questionFallback",
            MissingArgumentKey: "frequency_unit",
            QuickActions: new List<QuickAction>
            {
                new("habits.clarification.quickAction.daily",
                    """{"frequency_unit":"Day","frequency_quantity":1}"""),
                new("habits.clarification.quickAction.weekly",
                    """{"frequency_unit":"Week","frequency_quantity":1}"""),
                new("habits.clarification.quickAction.threePerWeek",
                    """{"frequency_unit":"Week","frequency_quantity":3,"is_flexible":true}"""),
                new("habits.clarification.quickAction.oneTime",
                    oneTimePatch),
            });
    }
}
