using Orbit.Application.Habits.Commands;
using static Orbit.Application.Chat.Tools.ToolArgumentNames;
using System.Text.Json;
using MediatR;
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

public class CreateHabitTool(IUserDateService userDateService, IMediator mediator) : IPreparedIntentTool, IClarificationPrecheckTool, ITransactionalAiTool, IHabitCreationCapacityTool
{
    private const string TitleProperty = "title";

    public string Name => "create_habit";

    public int Order => 0;

    public string Description =>
        HabitModelInstructions.WriteIntent + " Use frequency_unit: OneTime for one-time tasks; null leaves recurrence unspecified and may require clarification. Create habits or dated tasks with tags, goals, checklists and children inline. Single dated events are one-time, even when a weekday is named. Separate dated extras from recurring classes. Dated children inherit parent frequency without its weekdays; use OneTime for a one-time child. Preserve requested weekdays. Use checklist_items for steps done together and sub_habits for independent tracking.";

    public object GetParameterSchema() => BulkCreateHabitsTool.HabitItemSchema(AppConstants.MaxHabitDepth);

    public Task<Domain.Common.Result> CheckCreationCapacityAsync(JsonElement args, Guid userId, CancellationToken ct) =>
        HabitCreationCapacity.CheckAsync(mediator, userId,
            [OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema())], false, ct);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        if (!args.TryGetProperty(TitleProperty, out var titleEl) || string.IsNullOrWhiteSpace(titleEl.GetString()))
            return new ToolResult(false, Error: "title is required.");

        var title = titleEl.GetString() ?? string.Empty;
        var fieldError = BulkHabitToolArguments.CheckCreateHabitFields(args);
        if (fieldError is null && args.TryGetProperty(SubHabits, out var subFields) && subFields.ValueKind == JsonValueKind.Array)
            fieldError = subFields.EnumerateArray().Select(BulkHabitToolArguments.CheckHabitFields).FirstOrDefault(error => error is not null);
        if (fieldError is not null)
            return new ToolResult(false, Error: fieldError);

        if (NeedsClarification(args))
            return new ToolResult(true, EntityName: title, Payload: BuildFrequencyClarification(args));
        return await new BulkCreateHabitsTool(mediator).ExecuteSingleAsync(args, userId, ct);
    }

    public async Task<Domain.Common.Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        if (!args.TryGetProperty(TitleProperty, out var titleEl) || titleEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(titleEl.GetString()))
            return Domain.Common.Result.Failure("title is required.");

        var subHabits = args.TryGetProperty(SubHabits, out var subEl) && subEl.ValueKind == JsonValueKind.Array
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

        var children = BuildChildHabits(subHabits, userId, parent.Value, parent.Value.NextDueDate);
        return children.IsFailure ? children : Domain.Common.Result.Success();
    }

    public async Task<PreparedToolIntent?> PrepareIntentAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        args = OptionalToolArguments.OmitOptionalNulls(args, GetParameterSchema());
        if (NeedsClarification(args) || args.ValueKind != JsonValueKind.Object)
            return null;
        var node = JsonNode.Parse(args.GetRawText())!.AsObject();
        NormalizeMinuteTimes(node);
        if (node[SubHabits] is JsonArray childNodes)
            foreach (var child in childNodes.OfType<JsonObject>())
                NormalizeMinuteTimes(child);
        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var normalized = JsonSerializer.SerializeToElement(node);
        if (!ChatToolArgumentCheck.HasValidShape(this, normalized)
            || (await CheckArgumentsAsync(normalized, userId, ct)).IsFailure)
            return null;

        var creation = BuildCreationIntent(normalized, userId, today);
        if (creation is null)
            return null;
        node[DueDate] = creation.Definition.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (node[SubHabits] is JsonArray nodes)
            foreach (var (child, intent) in nodes.OfType<JsonObject>().Zip(creation.Children))
            {
                var definition = intent.Definition;
                child[DueDate] = definition.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                child["frequency_unit"] = definition.FrequencyUnit?.ToString() ?? "OneTime";
                if (definition.FrequencyQuantity is { } quantity)
                    child["frequency_quantity"] = quantity;
                if (definition.Days is { Count: > 0 } days)
                    child["days"] = JsonSerializer.SerializeToNode(days.Select(day => day.ToString()));
                if (definition.IntervalWeeks is { } interval)
                    child["interval_weeks"] = interval;
            }
        normalized = JsonSerializer.SerializeToElement(node);
        if ((await CheckArgumentsAsync(normalized, userId, ct)).IsFailure
            || !creation.Equals(BuildCreationIntent(normalized, userId, today)))
            return null;
        return PreparedToolIntent.Create(creation, normalized) with
        {
            ArgumentValidation = await CheckArgumentsAsync(args, userId, ct)
        };
    }

    private static HabitCreationIntent? BuildCreationIntent(JsonElement args, Guid userId, DateOnly today)
    {
        var parameters = BuildParentParameters(args, userId, args.GetProperty(TitleProperty).GetString()!, today);
        if (parameters.IsFailure)
            return null;
        var parent = Habit.Create(parameters.Value);
        if (parent.IsFailure)
            return null;
        var children = new List<HabitCreationIntent>();
        if (args.TryGetProperty(SubHabits, out var nodes))
            foreach (var sub in nodes.EnumerateArray())
            {
                var definition = BuildChildParameters(sub, userId, parent.Value, parent.Value.NextDueDate);
                if (definition.IsFailure || Habit.Create(definition.Value).IsFailure)
                    return null;
                children.Add(PreparedCreation(sub, definition.Value with { ParentHabitId = null }, []));
            }
        return PreparedCreation(args, parameters.Value, children);
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
            Days = PreparedToolIntent.Values(definition.Days ?? []),
            ReminderTimes = definition.ReminderTimes is { } times ? PreparedToolIntent.Values(times) : null,
            ChecklistItems = definition.ChecklistItems is { } checklist ? PreparedToolIntent.Values(checklist) : null,
            ScheduledReminders = definition.ScheduledReminders is { } scheduled ? PreparedToolIntent.Values(scheduled) : null,
            RelativeReminders = definition.RelativeReminders is { } relative ? PreparedToolIntent.Values(relative) : null
        },
        PreparedToolIntent.Values(JsonArgumentParser.ParseStringArray(args, "tag_names") ?? []),
        PreparedToolIntent.Values(JsonArgumentParser.ParseGuidArray(args, GoalIds) ?? []),
        PreparedToolIntent.Values(children));

    private sealed record HabitCreationIntent(HabitCreateParams Definition,
        IReadOnlyList<string> Tags, IReadOnlyList<Guid> Goals, IReadOnlyList<HabitCreationIntent> Children);

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
        var dueDate = JsonArgumentParser.ParseDateOnly(args, DueDate);
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

        var schedule = LegacyHabitSchedule.Resolve(JsonArgumentParser.GetOptionalBool(args, "is_general") ?? false, isFlexible,
            frequencyUnit, frequencyQuantity, isBadHabit, days, intervalWeeks);
        if (schedule.IsFailure)
            return schedule.PropagateError<HabitCreateParams>();

        return Domain.Common.Result.Success(new HabitCreateParams(schedule.Value,
            userId, title, frequencyUnit, frequencyQuantity,
            HabitScheduleService.InitialDueDate(today, dueDate, JsonArgumentParser.ParseDateOnly(args, StartDate), days),
            JsonArgumentParser.GetOptionalString(args, "description"),
            Days: days,
            IsBadHabit: isBadHabit,
            DueTime: dueTime,
            DueEndTime: JsonArgumentParser.ParseTimeOnly(args, "due_end_time"),
            ReminderEnabled: JsonArgumentParser.GetOptionalBool(args, "reminder_enabled") ?? RepeatReminderToolArguments.Parse(args) is not null,
            ReminderTimes: reminderTimes,
            SlipAlertEnabled: JsonArgumentParser.GetOptionalBool(args, "slip_alert_enabled") ?? isBadHabit,
            ChecklistItems: JsonArgumentParser.ParseChecklistItems(args),
            EndDate: JsonArgumentParser.ParseDateOnly(args, "end_date"),
            ScheduledReminders: scheduledReminders,
            Emoji: JsonArgumentParser.GetOptionalString(args, "emoji"),
            IntervalWeeks: intervalWeeks,
            RelativeReminders: JsonArgumentParser.ParseRelativeReminders(args),
            RepeatReminder: RepeatReminderToolArguments.Parse(args),
            StartDate: JsonArgumentParser.ParseDateOnly(args, StartDate)));
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
        var subDueDate = JsonArgumentParser.ParseDateOnly(sub, DueDate);
        var isOneTime = JsonArgumentParser.GetOptionalString(sub, "frequency_unit") == "OneTime";
        var hasExplicitCadence = isOneTime || HasExplicitCadence(subFrequencyUnit, subFrequencyQuantity, subIntervalWeeks, subDays);
        var frequencyUnit = isOneTime ? null : subFrequencyUnit ?? parent.FrequencyUnit;
        var frequencyQuantity = isOneTime ? null : subFrequencyQuantity ?? (subFrequencyUnit is not null ? 1 : parent.FrequencyQuantity);
        if (isOneTime)
            subDays = null;
        var isFlexible = JsonArgumentParser.GetOptionalBool(sub, "is_flexible") ?? false;
        frequencyUnit = NormalizeWeekdayUnit(frequencyUnit, frequencyQuantity, subDays, isFlexible);
        if (subDays is null && subDueDate is null && frequencyUnit == FrequencyUnit.Day && frequencyQuantity == 1)
            subDays = parent.Days.ToList();

        var schedule = LegacyHabitSchedule.Resolve(parent.Schedule == HabitSchedule.General, isFlexible, frequencyUnit,
            frequencyQuantity, JsonArgumentParser.GetOptionalBool(sub, "is_bad_habit") ?? false, subDays);
        if (schedule.IsFailure)
            return schedule.PropagateError<HabitCreateParams>();

        return Domain.Common.Result.Success(new HabitCreateParams(schedule.Value,
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
            ReminderEnabled: JsonArgumentParser.GetOptionalBool(sub, "reminder_enabled") ?? RepeatReminderToolArguments.Parse(sub) is not null,
            ReminderTimes: JsonArgumentParser.ParseIntArray(sub, "reminder_times"),
            SlipAlertEnabled: JsonArgumentParser.GetOptionalBool(sub, "slip_alert_enabled") ?? false,
            EndDate: JsonArgumentParser.ParseDateOnly(sub, "end_date"),
            ScheduledReminders: JsonArgumentParser.ParseScheduledReminders(sub),
            RelativeReminders: JsonArgumentParser.ParseRelativeReminders(sub),
            RepeatReminder: RepeatReminderToolArguments.Parse(sub),
            Emoji: JsonArgumentParser.GetOptionalString(sub, "emoji"),
            ChecklistItems: JsonArgumentParser.ParseChecklistItems(sub),
            IntervalWeeks: isOneTime ? null : subIntervalWeeks ?? (hasExplicitCadence ? null : parent.IntervalWeeks)));
    }

    private static bool HasExplicitCadence(FrequencyUnit? unit, int? quantity, int? interval, IReadOnlyList<DayOfWeek>? days) =>
        unit is not null || quantity is not null || interval is not null || days is not null;

    private static FrequencyUnit? NormalizeWeekdayUnit(
        FrequencyUnit? unit, int? quantity, IReadOnlyList<DayOfWeek>? days, bool isFlexible = false) =>
        !isFlexible && unit == FrequencyUnit.Week && quantity == 1 && days is { Count: > 0 }
            ? FrequencyUnit.Day
            : unit;

    private static readonly System.Text.RegularExpressions.Regex HabitWordRegex =
        new(
            @"\bhabit\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

    public bool NeedsClarification(JsonElement args)
    {
        if (args.TryGetProperty(RepeatReminderToolArguments.Field, out _)
            && JsonArgumentParser.GetOptionalBool(args, "is_general") == true)
            return false;
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
        var dueDate = JsonArgumentParser.GetOptionalString(args, DueDate);
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
