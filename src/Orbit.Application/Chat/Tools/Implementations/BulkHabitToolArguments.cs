using System.Globalization;
using System.Text.Json;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Enums;

namespace Orbit.Application.Chat.Tools.Implementations;

internal static class BulkHabitToolArguments
{
    private const string DueEndTimeField = "due_end_time";
    private const string IsGeneralField = "is_general";
    private const string SlipAlertEnabledField = "slip_alert_enabled";
    private const string RelativeRemindersField = "relative_reminders";
    public static (IReadOnlyList<(Guid HabitId, DateOnly Date)>? Items, string? Error)
        ParseRevisedDatedItems(JsonElement args)
    {
        if (!args.TryGetProperty("revised_items", out var values)
            || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
            return (null, "revised_items must be a non-empty array.");
        var items = new List<(Guid HabitId, DateOnly Date)>();
        var seen = new HashSet<(Guid, DateOnly)>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("habit_id", out var idValue)
                || idValue.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idValue.GetString(), out var id)
                || !value.TryGetProperty("date", out var dateValue)
                || dateValue.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(dateValue.GetString(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || !seen.Add((id, date)))
                return (null, "revised_items contains an invalid habit or date.");
            items.Add((id, date));
        }
        return (items, null);
    }

    public static (BulkHabitFilter? Filter, string? Error) ParseRequiredFilter(JsonElement args)
    {
        if (!args.TryGetProperty("filter", out var filterElement) || filterElement.ValueKind != JsonValueKind.Object)
            return (null, "filter is required and must be an object.");

        return ParseFilter(filterElement);
    }

    public static (BulkHabitFilter? Filter, string? Error) ParseEmojiFilter(JsonElement args)
    {
        if (args.TryGetProperty("filter", out var filterElement) && filterElement.ValueKind != JsonValueKind.Null)
        {
            if (filterElement.ValueKind != JsonValueKind.Object)
                return (null, "filter must be an object.");

            return ParseFilter(filterElement);
        }

        var ids = new List<Guid>();
        if (args.TryGetProperty("habit_ids", out var idsElement) && idsElement.ValueKind != JsonValueKind.Null)
        {
            if (idsElement.ValueKind != JsonValueKind.Array || idsElement.GetArrayLength() == 0)
                return (null, "habit_ids must be omitted or provided as a non-empty array of valid habit IDs.");

            foreach (var idElement in idsElement.EnumerateArray())
            {
                if (idElement.ValueKind != JsonValueKind.String || !Guid.TryParse(idElement.GetString(), out var id))
                    return (null, "habit_ids must contain only valid habit IDs.");
                ids.Add(id);
            }
        }

        var includeCompleted = JsonArgumentParser.GetOptionalBool(args, "include_completed") ?? false;
        return (new BulkHabitFilter(ids.Count == 0, ids, includeCompleted), null);
    }

    public static (BulkHabitFilter? Filter, string? Error) ParseActionFilter(JsonElement args)
    {
        var hasFilter = args.TryGetProperty("filter", out var filterElement)
            && filterElement.ValueKind != JsonValueKind.Null;
        var hasHabitIds = args.TryGetProperty("habit_ids", out var habitIdsElement)
            && habitIdsElement.ValueKind != JsonValueKind.Null;
        if (hasFilter && hasHabitIds)
            return (null, "filter cannot combine with habit_ids.");
        if (hasFilter)
            return ParseRequiredFilter(args);
        if (!hasHabitIds)
            return (null, "Provide filter or habit_ids.");

        var (filter, error) = ParseEmojiFilter(args);
        return error is null && filter is not null
            ? (filter with { IncludeCompleted = true }, null)
            : (null, error);
    }

    public static object ActionFilterSchema(string idsDescription, string dateDescription) => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            filter = FilterSchema(),
            habit_ids = new
            {
                type = JsonSchemaTypes.Array,
                items = new { type = JsonSchemaTypes.String },
                description = idsDescription
            },
            date = new
            {
                type = new[] { JsonSchemaTypes.String, "null" },
                description = dateDescription
            }
        },
        required = Array.Empty<string>()
    };

    public static (BulkHabitChanges? Changes, string? Error) ParseChanges(JsonElement args)
    {
        if (!args.TryGetProperty("updates", out var updates) || updates.ValueKind != JsonValueKind.Object)
            return (null, "updates is required and must be an object.");

        return ParseUpdateFields(updates);
    }

    public static (BulkHabitChanges? Changes, string? Error) ParseUpdateFields(JsonElement updates, bool allowHabitId = false)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "title", "description", "emoji", "frequency_unit", "frequency_quantity", "interval_weeks",
            "days", "due_date", "start_date", "end_date", "due_time", DueEndTimeField, "is_bad_habit", "is_flexible",
            IsGeneralField, SlipAlertEnabledField, RelativeRemindersField,
            "reminder_enabled", "reminder_times", "checklist_items", "scheduled_reminders", "clear_fields"
        };
        if (allowHabitId) allowed.Add("habit_id");
        if (updates.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            return (null, "updates contains an unsupported field.");
        var rawError = CheckHabitFields(updates);
        if (rawError is not null)
            return (null, rawError);
        var (resolvedUpdates, argumentError) = OptionalToolArguments.ResolveClears(updates,
            allowHabitId ? OptionalToolArguments.HabitClearFields : OptionalToolArguments.BulkHabitClearFields);
        if (argumentError is not null)
            return (null, argumentError);
        updates = resolvedUpdates;
        var fieldError = CheckHabitFields(updates);
        if (fieldError is not null)
            return (null, fieldError);

        var dateResult = ParseNullableDate(updates, "due_date", allowNull: false);
        var endDateResult = ParseNullableDate(updates, "end_date", allowNull: true);
        var timeResult = ParseNullableTime(updates, "due_time");
        var frequencyResult = ParseFrequency(updates);

        var changes = new BulkHabitChanges(
            HasTitle: updates.TryGetProperty("title", out _),
            Title: JsonArgumentParser.GetNullableString(updates, "title"),
            HasDescription: updates.TryGetProperty("description", out _),
            Description: JsonArgumentParser.GetNullableString(updates, "description"),
            HasEmoji: updates.TryGetProperty("emoji", out _),
            Emoji: JsonArgumentParser.GetNullableString(updates, "emoji"),
            HasFrequencyUnit: frequencyResult.IsSpecified,
            FrequencyUnit: frequencyResult.Value,
            HasFrequencyQuantity: updates.TryGetProperty("frequency_quantity", out _),
            FrequencyQuantity: JsonArgumentParser.GetOptionalInt(updates, "frequency_quantity"),
            HasIntervalWeeks: updates.TryGetProperty("interval_weeks", out _),
            IntervalWeeks: JsonArgumentParser.GetOptionalInt(updates, "interval_weeks"),
            HasDays: updates.TryGetProperty("days", out _),
            Days: updates.TryGetProperty("days", out _) ? JsonArgumentParser.ParseDays(updates) ?? [] : null,
            HasDueDate: dateResult.IsSpecified,
            DueDate: dateResult.Value,
            HasEndDate: endDateResult.IsSpecified,
            EndDate: endDateResult.Value,
            HasDueTime: timeResult.IsSpecified,
            DueTime: timeResult.Value,
            HasIsBadHabit: updates.TryGetProperty("is_bad_habit", out _),
            IsBadHabit: JsonArgumentParser.GetOptionalBool(updates, "is_bad_habit") ?? false,
            HasIsFlexible: updates.TryGetProperty("is_flexible", out _),
            IsFlexible: JsonArgumentParser.GetOptionalBool(updates, "is_flexible") ?? false,
            HasReminderEnabled: updates.TryGetProperty("reminder_enabled", out _),
            ReminderEnabled: JsonArgumentParser.GetOptionalBool(updates, "reminder_enabled") ?? false,
            HasReminderTimes: updates.TryGetProperty("reminder_times", out _),
            ReminderTimes: updates.TryGetProperty("reminder_times", out _) ? JsonArgumentParser.ParseIntArray(updates, "reminder_times") ?? [] : null,
            HasChecklistItems: updates.TryGetProperty("checklist_items", out _),
            ChecklistItems: updates.TryGetProperty("checklist_items", out _) ? JsonArgumentParser.ParseChecklistItems(updates) ?? [] : null,
            HasScheduledReminders: updates.TryGetProperty("scheduled_reminders", out _),
            ScheduledReminders: updates.TryGetProperty("scheduled_reminders", out _) ? JsonArgumentParser.ParseScheduledReminders(updates) ?? [] : null,
            HasDueEndTime: updates.TryGetProperty(DueEndTimeField, out _),
            DueEndTime: ParseNullableTime(updates, DueEndTimeField).Value,
            HasIsGeneral: updates.TryGetProperty(IsGeneralField, out _),
            IsGeneral: JsonArgumentParser.GetOptionalBool(updates, IsGeneralField) ?? false,
            HasSlipAlertEnabled: updates.TryGetProperty(SlipAlertEnabledField, out _),
            SlipAlertEnabled: JsonArgumentParser.GetOptionalBool(updates, SlipAlertEnabledField) ?? false,
            HasRelativeReminders: updates.TryGetProperty(RelativeRemindersField, out _),
            RelativeReminders: JsonArgumentParser.ParseRelativeReminders(updates),
            HasStartDate: updates.TryGetProperty("start_date", out _),
            StartDate: JsonArgumentParser.ParseDateOnly(updates, "start_date"));

        return (changes, null);
    }

    /// <summary>
    /// The strict read of the habit fields: every value has the kind, the enum member, the
    /// weekday name and the date or time format that the habit writes expect. Fields that are
    /// not habit fields are left to their tool.
    /// </summary>
    public static string? CheckHabitFields(JsonElement fields)
    {
        return ValidateUpdateKinds(fields)
            ?? ParseNullableDate(fields, "due_date", allowNull: false).Error
            ?? ParseNullableDate(fields, "start_date", allowNull: false).Error
            ?? ParseNullableDate(fields, "end_date", allowNull: true).Error
            ?? ParseNullableTime(fields, "due_time").Error
            ?? ParseNullableTime(fields, DueEndTimeField).Error
            ?? ParseFrequency(fields).Error;
    }

    public static string? CheckCreateHabitFields(JsonElement fields)
    {
        var fieldError = CheckHabitFields(fields);
        if (fieldError is not null)
            return fieldError;
        var semantics = new Validators.HabitProposalValidator().Validate(new Validators.HabitProposal(string.Empty, fields));
        return semantics.IsValid ? null : semantics.Errors[0].ErrorMessage;
    }

    public static object FilterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        description = "Select all matches or use predicates.",
        properties = new
        {
            all = new { type = JsonSchemaTypes.Boolean, description = "Select every match." },
            habit_ids = new { type = JsonSchemaTypes.Array, items = new { type = JsonSchemaTypes.String } },
            tag = new { type = JsonSchemaTypes.String, description = "Exact tag name, case-insensitive." },
            search = new { type = JsonSchemaTypes.String, description = "Match every search word to a title word prefix, ignoring case, accents and order. Excludes descriptions." },
            is_completed = new { type = JsonSchemaTypes.Boolean, description = "true: completed; false: active (default)." },
            is_general = new { type = JsonSchemaTypes.Boolean },
            is_bad_habit = new { type = JsonSchemaTypes.Boolean },
            frequency = new { type = JsonSchemaTypes.String, @enum = new[] { "Day", "Week", "Month", "Year", "OneTime" } }
        }
    };

    private static (BulkHabitFilter? Filter, string? Error) ParseFilter(JsonElement filter)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "all", "habit_ids", "tag", "search", "is_completed", IsGeneralField, "is_bad_habit", "frequency"
        };
        if (filter.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            return (null, "filter contains an unsupported field.");
        filter = OptionalToolArguments.OmitNulls(filter);
        foreach (var booleanName in new[] { "all", "is_completed", IsGeneralField, "is_bad_habit" })
        {
            if (filter.TryGetProperty(booleanName, out var booleanValue)
                && booleanValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                return (null, $"filter.{booleanName} must be a boolean.");
        }
        foreach (var stringName in new[] { "tag", "search" })
        {
            if (filter.TryGetProperty(stringName, out var stringValue) && stringValue.ValueKind != JsonValueKind.String)
                return (null, $"filter.{stringName} must be a string.");
        }

        var ids = new List<Guid>();
        if (filter.TryGetProperty("habit_ids", out var idsElement))
        {
            if (idsElement.ValueKind != JsonValueKind.Array)
                return (null, "filter.habit_ids must be an array of valid habit IDs.");
            foreach (var idElement in idsElement.EnumerateArray())
            {
                if (idElement.ValueKind != JsonValueKind.String || !Guid.TryParse(idElement.GetString(), out var id))
                    return (null, "filter.habit_ids must contain only valid habit IDs.");
                ids.Add(id);
            }
        }

        FrequencyUnit? frequency = null;
        var oneTime = false;
        if (filter.TryGetProperty("frequency", out var frequencyElement))
        {
            if (frequencyElement.ValueKind != JsonValueKind.String)
                return (null, "filter.frequency must be Day, Week, Month, Year, or OneTime.");
            var value = frequencyElement.GetString();
            if (string.Equals(value, "OneTime", StringComparison.OrdinalIgnoreCase))
                oneTime = true;
            else if (Enum.TryParse<FrequencyUnit>(value, true, out var parsedFrequency))
                frequency = parsedFrequency;
            else
                return (null, "filter.frequency must be Day, Week, Month, Year, or OneTime.");
        }

        var result = new BulkHabitFilter(
            All: JsonArgumentParser.GetOptionalBool(filter, "all") ?? false,
            HabitIds: ids,
            Tag: JsonArgumentParser.GetNullableString(filter, "tag"),
            Search: JsonArgumentParser.GetNullableString(filter, "search"),
            IsGeneral: JsonArgumentParser.GetOptionalBool(filter, IsGeneralField),
            IsBadHabit: JsonArgumentParser.GetOptionalBool(filter, "is_bad_habit"),
            Frequency: frequency,
            OneTime: oneTime,
            IsCompleted: JsonArgumentParser.GetOptionalBool(filter, "is_completed"));

        if (result.All && result.HabitIds.Count > 0)
            return (null, "filter cannot combine all=true with habit_ids.");

        return result.HasSelector
            ? (result, null)
            : (null, "filter must explicitly select all habits or provide at least one predicate.");
    }

    private static (bool IsSpecified, DateOnly? Value, string? Error) ParseNullableDate(
        JsonElement element,
        string propertyName,
        bool allowNull)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return (false, null, null);
        if (property.ValueKind == JsonValueKind.Null && allowNull)
            return (true, null, null);
        if (property.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(property.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return (false, null, $"{propertyName} must be a date in YYYY-MM-DD format{(allowNull ? " or null" : string.Empty)}.");
        }
        return (true, value, null);
    }

    private static (bool IsSpecified, TimeOnly? Value, string? Error) ParseNullableTime(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return (false, null, null);
        if (property.ValueKind == JsonValueKind.Null)
            return (true, null, null);
        if (property.ValueKind != JsonValueKind.String
            || !TimeOnly.TryParseExact(property.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return (false, null, $"{propertyName} must use HH:mm format or null.");
        return (true, value, null);
    }

    private static (bool IsSpecified, FrequencyUnit? Value, string? Error) ParseFrequency(JsonElement element)
    {
        if (!element.TryGetProperty("frequency_unit", out var property))
            return (false, null, null);
        if (property.ValueKind == JsonValueKind.Null || property.ValueKind == JsonValueKind.String && property.GetString() == "OneTime")
            return (true, null, null);
        if (property.ValueKind != JsonValueKind.String
            || !Enum.TryParse<FrequencyUnit>(property.GetString(), true, out var value))
            return (false, null, "frequency_unit must be Day, Week, Month, Year, or null.");
        return (true, value, null);
    }

    private static string? ValidateUpdateKinds(JsonElement updates)
    {
        return ValidateStringKinds(updates)
            ?? ValidateIntegerKinds(updates)
            ?? ValidateBooleanKinds(updates)
            ?? ValidateArrayKinds(updates)
            ?? ValidateArrayContents(updates);
    }

    private static string? ValidateStringKinds(JsonElement updates)
    {
        if (!HasKind(updates, "title", JsonValueKind.String))
            return "title must be a string.";
        foreach (var nullableString in new[] { "description", "emoji" })
        {
            if (!HasKind(updates, nullableString, JsonValueKind.String, JsonValueKind.Null))
                return $"{nullableString} must be a string or null.";
        }
        return null;
    }

    private static string? ValidateIntegerKinds(JsonElement updates)
    {
        foreach (var nullableInteger in new[] { "frequency_quantity", "interval_weeks" })
        {
            if (!HasKind(updates, nullableInteger, JsonValueKind.Number, JsonValueKind.Null))
                return $"{nullableInteger} must be an integer or null.";
            if (updates.TryGetProperty(nullableInteger, out var number)
                && number.ValueKind == JsonValueKind.Number
                && !number.TryGetInt32(out _))
                return $"{nullableInteger} must be an integer or null.";
        }
        return null;
    }

    private static string? ValidateBooleanKinds(JsonElement updates)
    {
        var booleanName = new[] { "is_bad_habit", "is_flexible", "reminder_enabled", IsGeneralField, SlipAlertEnabledField }
            .FirstOrDefault(name => !HasKind(updates, name, JsonValueKind.True, JsonValueKind.False));
        return booleanName is null ? null : $"{booleanName} must be a boolean.";
    }

    private static string? ValidateArrayKinds(JsonElement updates)
    {
        var arrayName = new[] { "days", "reminder_times", "checklist_items", "scheduled_reminders", RelativeRemindersField }
            .FirstOrDefault(name => !HasKind(updates, name, JsonValueKind.Array));
        return arrayName is null ? null : $"{arrayName} must be an array.";
    }

    private static string? ValidateArrayContents(JsonElement updates)
    {
        return ValidateDays(updates)
            ?? ValidateReminderTimes(updates)
            ?? ValidateChecklistItems(updates)
            ?? ValidateScheduledReminders(updates)
            ?? ValidateRelativeReminders(updates);
    }

    private static string? ValidateDays(JsonElement updates)
    {
        if (updates.TryGetProperty("days", out var days)
            && days.EnumerateArray().Any(day => day.ValueKind != JsonValueKind.String
                || !Enum.TryParse<DayOfWeek>(day.GetString(), true, out _)))
            return "days must contain valid weekday names.";
        return null;
    }

    private static string? ValidateReminderTimes(JsonElement updates)
    {
        if (updates.TryGetProperty("reminder_times", out var reminderTimes)
            && reminderTimes.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
            return "reminder_times must contain only integers.";
        return null;
    }

    private static string? ValidateChecklistItems(JsonElement updates)
    {
        if (updates.TryGetProperty("checklist_items", out var checklistItems)
            && checklistItems.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                || (item.TryGetProperty("is_checked", out var isChecked)
                    && isChecked.ValueKind is not JsonValueKind.True and not JsonValueKind.False)))
            return "checklist_items contains an invalid item.";
        return null;
    }

    private static string? ValidateScheduledReminders(JsonElement updates)
    {
        if (updates.TryGetProperty("scheduled_reminders", out var scheduledReminders)
            && scheduledReminders.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("when", out var when) || when.ValueKind != JsonValueKind.String
                || !JsonArgumentParser.TryParseScheduledReminderWhen(when.GetString() ?? string.Empty, out _)
                || !item.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.String
                || !TimeOnly.TryParseExact(time.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            return "scheduled_reminders contains an invalid item.";
        return null;
    }

    private static string? ValidateRelativeReminders(JsonElement fields)
    {
        if (!fields.TryGetProperty(RelativeRemindersField, out var reminders))
            return null;
        return reminders.EnumerateArray().Select(ValidateRelativeReminder).FirstOrDefault(error => error is not null);
    }

    private static string? ValidateRelativeReminder(JsonElement reminder)
    {
        if (reminder.ValueKind != JsonValueKind.Object
            || reminder.EnumerateObject().Any(p => p.Name is not ("minutes_before" or "when" or "time")))
            return "relative_reminders must contain reminder objects.";
        if (reminder.TryGetProperty("minutes_before", out var minutes))
            return reminder.TryGetProperty("when", out _) || reminder.TryGetProperty("time", out _)
                || minutes.ValueKind != JsonValueKind.Number || !minutes.TryGetInt32(out _)
                ? "relative_reminders requires minutes_before or when and time." : null;
        return ValidateRelativeClock(reminder);
    }

    private static string? ValidateRelativeClock(JsonElement reminder) =>
        !reminder.TryGetProperty("when", out var when) || when.ValueKind != JsonValueKind.String
            || !JsonArgumentParser.TryParseScheduledReminderWhen(when.GetString()!, out _)
            || !reminder.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.String
            || ParseNullableTime(reminder, "time").Error is not null
            ? "relative_reminders requires minutes_before or when and time." : null;

    public static object ScheduledRemindersSchema() => new
    {
        type = JsonSchemaTypes.Array,
        description = "Absolute-time reminders for habits WITHOUT a due_time",
        items = new
        {
            type = JsonSchemaTypes.Object,
            properties = new
            {
                when = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.ScheduledReminderWhenEnum },
                time = new { type = JsonSchemaTypes.String, description = "HH:mm 24h format" }
            },
            required = new[] { "when", "time" }
        }
    };

    public static object ChecklistItemsSchema() => new
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
    };

    public static object RelativeRemindersSchema() => new
    {
        type = JsonSchemaTypes.Array,
        description = "Reminders relative to due_time: minutes_before, or when (day_before/same_day) and time (HH:mm). Empty array clears them.",
        items = new
        {
            type = JsonSchemaTypes.Object,
            properties = new
            {
                minutes_before = new { type = JsonSchemaTypes.Integer },
                when = new { type = JsonSchemaTypes.String, @enum = JsonSchemaTypes.ScheduledReminderWhenEnum },
                time = new { type = JsonSchemaTypes.String }
            }
        }
    };

    private static bool HasKind(JsonElement element, string propertyName, params JsonValueKind[] allowedKinds)
    {
        return !element.TryGetProperty(propertyName, out var property)
            || allowedKinds.Contains(property.ValueKind);
    }
}
