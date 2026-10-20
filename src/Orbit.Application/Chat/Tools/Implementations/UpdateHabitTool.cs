using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

public class UpdateHabitTool(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    IPayGateService payGate) : IAiTool, IArgumentCheckTool
{
    public string Name => "update_habit";

    public string Description =>
        "Update habit fields. Null keeps values; clear_fields removes them (frequency_unit makes a one-time task). Use is_flexible for windows and bulk tools for multiple habits.";

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            clear_fields = OptionalToolArguments.ClearFieldsSchema(OptionalToolArguments.HabitClearFields),
            habit_id = new { type = JsonSchemaTypes.String },
            title = new { type = JsonSchemaTypes.String },
            description = new { type = JsonSchemaTypes.String, description = "New description", nullable = true },
            emoji = new { type = JsonSchemaTypes.String, description = "Emoji used as the habit icon. Use clear_fields to clear.", nullable = true },
            frequency_unit = new
            {
                type = JsonSchemaTypes.String,
                description = "New recurrence unit. Clear frequency_unit via clear_fields for a one-time task.",
                nullable = true,
                @enum = JsonSchemaTypes.FrequencyUnitEnum
            },
            frequency_quantity = new { type = JsonSchemaTypes.Integer, description = "New frequency quantity" },
            interval_weeks = new { type = JsonSchemaTypes.Integer, description = "Repeat interval in weeks, minimum 1." },
            days = new
            {
                type = JsonSchemaTypes.Array,
                items = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.WeekdayEnum }
            },
            due_date = new { type = JsonSchemaTypes.String, description = "New due date (YYYY-MM-DD)" },
            end_date = new { type = JsonSchemaTypes.String, description = "YYYY-MM-DD cutoff; clear via clear_fields.", nullable = true },
            due_time = new { type = JsonSchemaTypes.String, description = "New due time (HH:mm). Use clear_fields to clear.", nullable = true },
            is_bad_habit = new { type = JsonSchemaTypes.Boolean, description = "Whether this is a bad habit" },
            is_flexible = new { type = JsonSchemaTypes.Boolean, description = "True for window-based tracking. Cannot have days set." },
            reminder_enabled = new { type = JsonSchemaTypes.Boolean, description = "Enable or disable reminders" },
            reminder_times = new
            {
                type = JsonSchemaTypes.Array,
                description = "Minutes before due_time.",
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
            }
        },
        required = new[] { "habit_id" }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (resolvedArgs, argumentError) = OptionalToolArguments.ResolveClears(args, OptionalToolArguments.HabitClearFields);
        if (argumentError is not null)
            return new ToolResult(false, Error: argumentError);
        args = resolvedArgs;

        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return HabitToolHelpers.InvalidHabitIdResult();

        var result = await HabitCeilingLock.ExecuteEntryAsync<UpdateToolState, ToolResult>(
            unitOfWork,
            userId,
            payGate,
            transactionToken => PrepareUpdateAsync(args, habitId, userId, transactionToken),
            state => HabitLiveRootEntry.FromUpdate(state.Habit, state.Update),
            ApplyUpdateAsync,
            ct);

        if (result.IsFailure)
            return ToolResult.FromFailure(result);

        return result.Value;
    }

    public async Task<Result> CheckArgumentsAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var (resolvedArgs, argumentError) = OptionalToolArguments.ResolveClears(args, OptionalToolArguments.HabitClearFields);
        if (argumentError is not null)
            return Result.Failure(argumentError);
        args = resolvedArgs;

        if (!HabitToolHelpers.TryParseHabitId(args, out var habitId))
            return Result.Failure("habit_id is required and must be a valid GUID.");

        var fieldError = BulkHabitToolArguments.CheckHabitFields(args);
        if (fieldError is not null)
            return Result.Failure(fieldError);

        var habits = await habitRepository.FindAsync(
            habit => habit.Id == habitId && habit.UserId == userId, ct);
        var habit = habits.FirstOrDefault();
        if (habit is null)
            return Result.Failure($"Habit {habitId} not found.");

        var today = await userDateService.GetUserTodayAsync(userId, ct);
        var preview = habit.PreviewUpdate(ResolveUpdateParams(args, habit, today));
        return preview.IsFailure ? preview : Result.Success();
    }

    private async Task<Result<UpdateToolState>> PrepareUpdateAsync(
        JsonElement args,
        Guid habitId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var habit = await HabitToolHelpers.FindHabitAsync(
            habitRepository, habitId, userId, cancellationToken);
        if (habit is null)
            return Result.Failure<UpdateToolState>($"Habit {habitId} not found.");

        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        return Result.Success(new UpdateToolState(habit, ResolveUpdateParams(args, habit, today)));
    }

    private async Task<Result<ToolResult>> ApplyUpdateAsync(
        UpdateToolState state,
        CancellationToken cancellationToken)
    {
        var result = state.Habit.Update(state.Update);
        if (result.IsFailure)
            return result.PropagateError<ToolResult>();

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(new ToolResult(
            true,
            EntityId: state.Habit.Id.ToString(),
            EntityName: state.Habit.Title));
    }

    private sealed record UpdateToolState(Habit Habit, HabitUpdateParams Update);

    /// <summary>
    /// Resolve prepared fields after optional nulls and explicit clear_fields are separated.
    /// Extracted to reduce ExecuteAsync cognitive complexity.
    /// </summary>
    private static HabitUpdateParams ResolveUpdateParams(JsonElement args, Habit habit, DateOnly today)
    {
        var title = ResolveTitle(args, habit);
        var description = ResolveDescription(args, habit);
        var (frequencyUnit, frequencyQuantity) = ResolveFrequency(args, habit);
        var intervalWeeks = ResolveIntervalWeeks(args, habit);
        var days = ResolveDays(args, habit);
        var isBadHabit = ResolveBoolField(args, "is_bad_habit", habit.IsBadHabit);
        var dueDate = ResolveDueDate(args, habit);
        var dueTime = ResolveDueTime(args, habit);
        var (endDate, clearEndDate) = ResolveEndDate(args);

        var (reminderTimes, scheduledReminders) = ReminderStoreNormalizer.NormalizeForUpdate(
            newDueTime: dueTime,
            previousDueTime: habit.DueTime,
            suppliedReminderTimes: ResolveOptionalArray(args, "reminder_times"),
            suppliedScheduledReminders: ResolveOptionalScheduledReminders(args),
            existingReminderTimes: habit.ReminderTimes,
            existingScheduledReminders: habit.ScheduledReminders);

        return new HabitUpdateParams(
            title, description, frequencyUnit, frequencyQuantity, days, isBadHabit, dueDate,
            DueTime: dueTime,
            DueEndTime: habit.DueEndTime,
            ReminderEnabled: ResolveOptionalBool(args, "reminder_enabled"),
            ReminderTimes: reminderTimes,
            ChecklistItems: ResolveOptionalChecklist(args),
            IsFlexible: ResolveOptionalBool(args, "is_flexible"),
            EndDate: endDate,
            ClearEndDate: clearEndDate,
            ScheduledReminders: scheduledReminders,
            Emoji: ResolveEmoji(args, habit),
            UserToday: today,
            IntervalWeeks: intervalWeeks);
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

    private static int? ResolveIntervalWeeks(JsonElement args, Habit habit) =>
        JsonArgumentParser.PropertyExists(args, "interval_weeks")
            ? JsonArgumentParser.GetOptionalInt(args, "interval_weeks") ?? habit.IntervalWeeks
            : habit.IntervalWeeks;

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
