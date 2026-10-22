using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using Orbit.Application.Chat.Models;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Habits.Services;
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
    IUnitOfWork unitOfWork) : IPreparedIntentTool, IClarificationPrecheckTool
{
    private const string TitleProperty = "title";

    public string Name => "create_habit";

    public int Order => 0;

    public string Description =>
        "Use frequency_unit: OneTime for one-time tasks; null leaves recurrence unspecified and may require clarification. Create habits or dated tasks with tags, goals, checklists and children inline. Single dated events are one-time, even when a weekday is named. Separate dated extras from recurring classes. Dated children inherit parent frequency without its weekdays; use OneTime for a one-time child. Preserve requested weekdays. Use checklist_items for steps done together and sub_habits for independent tracking.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            title = new { type = JsonSchemaTypes.String },
            description = new { type = JsonSchemaTypes.String },
            emoji = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "Icon emoji. Use the requested emoji or a relevant one." },
            frequency_unit = new
            {
                type = new[] { JsonSchemaTypes.String, "null" },
                description = "OneTime for explicit one-time tasks; null leaves recurrence unspecified.",
                @enum = JsonSchemaTypes.FrequencyOrOneTimeEnum
            },
            frequency_quantity = new { type = JsonSchemaTypes.Integer, description = "How often (e.g. every 2 days). Defaults to 1." },
            interval_weeks = new { type = JsonSchemaTypes.Integer, description = "Repeat interval in weeks. Omit for every week. Minimum 1." },
            days = new
            {
                type = JsonSchemaTypes.Array,
                description = "Recurring weekdays with Day and quantity 1; Week with quantity 1 normalizes to Day. Null for one-time tasks.",
                items = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.WeekdayEnum }
            },
            due_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. One-time due date or recurrence start. Defaults to today." },
            end_date = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "YYYY-MM-DD. Optional end date. Habit stops appearing after this date. Only for recurring habits." },
            due_time = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "HH:mm start time. Omit when there is only a deadline." },
            due_end_time = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "HH:mm end time or deadline. For 'before 15h' without a fixed start, set 15:00 and omit due_time." },
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
                description = "Steps done together, such as shopping, prep or packing lists. Independent activities use sub_habits.",
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
                description = "Children with independent schedules or streaks. Steps done together use checklist_items.",
                items = new
                {
                    type = JsonSchemaTypes.Object,
                    properties = new
                    {
                        title = new { type = JsonSchemaTypes.String },
                        description = new { type = JsonSchemaTypes.String },
                        emoji = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "Relevant icon emoji." },
                        frequency_unit = new { type = new[] { JsonSchemaTypes.String, "null" }, description = "Override parent recurrence. Use OneTime for a one-time task; null inherits. Otherwise the parent's unit and quantity are inherited.", @enum = JsonSchemaTypes.FrequencyOrOneTimeEnum },
                        frequency_quantity = new { type = JsonSchemaTypes.Integer },
                        interval_weeks = new { type = JsonSchemaTypes.Integer, description = "Repeat interval in weeks. Minimum 1." },
                        days = new { type = JsonSchemaTypes.Array, description = "Recurring weekdays with Day (or Week normalized to Day) and quantity 1. Omit for dated one-time tasks.", items = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.WeekdayEnum } },
                        due_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD. Child start date without inherited weekdays. Use OneTime for a one-time child." },
                        due_time = new { type = JsonSchemaTypes.String, description = "Start time (HH:mm)", nullable = true },
                        due_end_time = new { type = JsonSchemaTypes.String, description = "End time or deadline (HH:mm)", nullable = true },
                        end_date = new { type = JsonSchemaTypes.String, description = "End date (YYYY-MM-DD)", nullable = true },
                        reminder_enabled = new { type = JsonSchemaTypes.Boolean },
                        reminder_times = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.Integer } },
                        slip_alert_enabled = new { type = JsonSchemaTypes.Boolean },
                        is_flexible = new { type = JsonSchemaTypes.Boolean },
                        tag_names = new { type = JsonSchemaTypes.Array, description = "Tag names for this child", items = new { type = JsonSchemaTypes.String } },
                        relative_reminders = BulkHabitToolArguments.RelativeRemindersSchema(),
                        scheduled_reminders = BulkHabitToolArguments.ScheduledRemindersSchema(),
                        is_bad_habit = new { type = JsonSchemaTypes.Boolean },
                        checklist_items = BulkHabitToolArguments.ChecklistItemsSchema()
                    },
                    required = new[] { TitleProperty }
                }
            }
        },
        required = new[] { TitleProperty }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
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
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
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

    public async Task<PreparedToolIntent?> PrepareIntentAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        if (NeedsClarification(args) || args.ValueKind != JsonValueKind.Object)
            return null;
        var node = JsonNode.Parse(args.GetRawText())!.AsObject();
        NormalizeMinuteTimes(node);
        if (node["sub_habits"] is JsonArray childNodes)
            foreach (var child in childNodes.OfType<JsonObject>())
                NormalizeMinuteTimes(child);
        var today = await userDateService.GetUserTodayAsync(userId, ct);
        node["due_date"] ??= today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var normalized = JsonSerializer.SerializeToElement(node);
        if (!ChatToolArgumentCheck.HasValidShape(this, normalized)
            || (await CheckArgumentsAsync(normalized, userId, ct)).IsFailure)
            return null;

        var parameters = BuildParentParameters(normalized, userId, normalized.GetProperty(TitleProperty).GetString()!, today).Value;
        var parent = Habit.Create(parameters).Value;
        var children = new List<HabitCreationIntent>();
        if (node["sub_habits"] is JsonArray nodes)
            foreach (var child in nodes.OfType<JsonObject>())
            {
                var sub = JsonSerializer.SerializeToElement(child);
                var definition = BuildChildParameters(sub, userId, parent, today).Value;
                children.Add(PreparedCreation(sub, definition with { ParentHabitId = null }, []));
                child["due_date"] = definition.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                child["frequency_unit"] = definition.FrequencyUnit?.ToString() ?? "OneTime";
                if (definition.FrequencyQuantity is { } quantity)
                    child["frequency_quantity"] = quantity;
                if (definition.Days is { Count: > 0 } days)
                    child["days"] = JsonSerializer.SerializeToNode(days.Select(day => day.ToString()));
                if (definition.IntervalWeeks is { } interval)
                    child["interval_weeks"] = interval;
            }
        normalized = JsonSerializer.SerializeToElement(node);
        if ((await CheckArgumentsAsync(normalized, userId, ct)).IsFailure)
            return null;
        return PreparedToolIntent.Create(PreparedCreation(normalized, parameters, children), normalized) with
        {
            ArgumentValidation = await CheckArgumentsAsync(args, userId, ct)
        };
    }

    private static void NormalizeMinuteTimes(JsonObject node)
    {
        foreach (var field in new[] { "due_time", "due_end_time" })
            if (node[field] is JsonValue value && value.TryGetValue<string>(out var text)
                && TimeOnly.TryParseExact(text, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                && time.Second == 0)
                node[field] = time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static HabitCreationIntent PreparedCreation(JsonElement args, HabitCreateParams definition,
        IEnumerable<HabitCreationIntent> children) => new(definition with
        {
            Days = definition.Days is { } days ? PreparedToolIntent.Values(days) : null,
            ReminderTimes = definition.ReminderTimes is { } times ? PreparedToolIntent.Values(times) : null,
            ChecklistItems = definition.ChecklistItems is { } checklist ? PreparedToolIntent.Values(checklist) : null,
            ScheduledReminders = definition.ScheduledReminders is { } scheduled ? PreparedToolIntent.Values(scheduled) : null,
            RelativeReminders = definition.RelativeReminders is { } relative ? PreparedToolIntent.Values(relative) : null
        },
        PreparedToolIntent.Values(JsonArgumentParser.ParseStringArray(args, "tag_names") ?? []),
        PreparedToolIntent.Values(JsonArgumentParser.ParseGuidArray(args, "goal_ids") ?? []),
        PreparedToolIntent.Values(children));

    private sealed record HabitCreationIntent(HabitCreateParams Definition,
        IReadOnlyList<string> Tags, IReadOnlyList<Guid> Goals, IReadOnlyList<HabitCreationIntent> Children);

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

        var featureGate = await HabitCreationGates.CheckAsync(habit, payGate, ct);
        if (featureGate.IsFailure)
            return ToolResult.FromFailure(featureGate);

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
        var parameters = BuildParentParameters(args, userId, title, today);
        return parameters.IsFailure ? parameters.PropagateError<Habit>() : Habit.Create(parameters.Value);
    }

    private static Domain.Common.Result<HabitCreateParams> BuildParentParameters(
        JsonElement args, Guid userId, string title, DateOnly today)
    {
        var parsedFrequency = JsonArgumentParser.ParseFrequencyUnit(args);
        if (parsedFrequency.IsFailure)
            return parsedFrequency.PropagateError<HabitCreateParams>();
        var frequencyUnit = parsedFrequency.Value;
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

        return Domain.Common.Result.Success(new HabitCreateParams(
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
            var childResult = BuildChildHabit(sub, userId, parent, parentDueDate);
            if (childResult.IsFailure)
                return childResult.PropagateError<List<Habit>>();

            children.Add(childResult.Value);
        }

        return Domain.Common.Result.Success(children);
    }

    private static Domain.Common.Result<Habit> BuildChildHabit(
        JsonElement sub, Guid userId, Habit parent, DateOnly parentDueDate)
    {
        var parameters = BuildChildParameters(sub, userId, parent, parentDueDate);
        return parameters.IsFailure ? parameters.PropagateError<Habit>() : Habit.Create(parameters.Value);
    }

    private static Domain.Common.Result<HabitCreateParams> BuildChildParameters(
        JsonElement sub, Guid userId, Habit parent, DateOnly parentDueDate)
    {
        var parsedFrequency = JsonArgumentParser.ParseFrequencyUnit(sub);
        if (parsedFrequency.IsFailure)
            return parsedFrequency.PropagateError<HabitCreateParams>();
        var subFrequencyUnit = parsedFrequency.Value;
        var subFrequencyQuantity = JsonArgumentParser.GetOptionalInt(sub, "frequency_quantity");
        var subIntervalWeeks = JsonArgumentParser.GetOptionalInt(sub, "interval_weeks");
        var subDays = JsonArgumentParser.ParseDays(sub);
        var subDueDate = JsonArgumentParser.ParseDateOnly(sub, "due_date");
        var isOneTime = JsonArgumentParser.GetOptionalString(sub, "frequency_unit") == "OneTime";
        var hasExplicitCadence = isOneTime || subFrequencyUnit is not null
            || subFrequencyQuantity is not null
            || subIntervalWeeks is not null
            || subDays is not null;
        var frequencyUnit = isOneTime ? null : subFrequencyUnit ?? parent.FrequencyUnit;
        var frequencyQuantity = isOneTime ? null : subFrequencyQuantity ?? (subFrequencyUnit is not null ? 1 : parent.FrequencyQuantity);
        if (isOneTime)
            subDays = null;
        var isFlexible = JsonArgumentParser.GetOptionalBool(sub, "is_flexible") ?? false;
        frequencyUnit = NormalizeWeekdayUnit(frequencyUnit, frequencyQuantity, subDays, isFlexible);
        if (subDays is null && subDueDate is null && frequencyUnit == FrequencyUnit.Day && frequencyQuantity == 1)
            subDays = parent.Days.ToList();

        return Domain.Common.Result.Success(new HabitCreateParams(
            userId,
            JsonArgumentParser.GetOptionalString(sub, TitleProperty) ?? "Untitled",
            frequencyUnit,
            frequencyQuantity,
            subDueDate ?? parentDueDate,
            JsonArgumentParser.GetOptionalString(sub, "description"),
            Days: subDays,
            IsBadHabit: JsonArgumentParser.GetOptionalBool(sub, "is_bad_habit") ?? false,
            ParentHabitId: parent.Id,
            DueTime: JsonArgumentParser.ParseTimeOnly(sub, "due_time"),
            DueEndTime: JsonArgumentParser.ParseTimeOnly(sub, "due_end_time"),
            ReminderEnabled: JsonArgumentParser.GetOptionalBool(sub, "reminder_enabled") ?? false,
            ReminderTimes: JsonArgumentParser.ParseIntArray(sub, "reminder_times"),
            SlipAlertEnabled: JsonArgumentParser.GetOptionalBool(sub, "slip_alert_enabled") ?? false,
            IsFlexible: isFlexible,
            IsGeneral: parent.IsGeneral,
            EndDate: JsonArgumentParser.ParseDateOnly(sub, "end_date"),
            ScheduledReminders: JsonArgumentParser.ParseScheduledReminders(sub),
            RelativeReminders: JsonArgumentParser.ParseRelativeReminders(sub),
            Emoji: JsonArgumentParser.GetOptionalString(sub, "emoji"),
            ChecklistItems: JsonArgumentParser.ParseChecklistItems(sub),
            IntervalWeeks: isOneTime ? null : subIntervalWeeks ?? (hasExplicitCadence ? null : parent.IntervalWeeks)));
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
        return (!args.TryGetProperty("frequency_unit", out var frequency) || frequency.ValueKind == JsonValueKind.Null)
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
            ? """{"frequency_unit":"OneTime"}"""
            : JsonSerializer.Serialize(new { frequency_unit = "OneTime", due_date = dueDate });
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
