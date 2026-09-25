using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat;

public sealed class PendingOperationChangePreviewer(
    IGenericRepository<Habit> habitRepository,
    IUserDateService userDateService) : IPendingOperationChangePreviewer
{
    private const int MaxDisplayedEntries = 3;
    private const int MaxEntryLength = 60;

    public async Task<PendingOperationChangePreview?> PreviewAsync(
        Guid userId,
        string operationId,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (operationId is not ("bulk_update_habits" or "bulk_reschedule_habits"))
            return null;

        var (filter, filterError) = BulkHabitToolArguments.ParseRequiredFilter(arguments);
        if (filterError is not null || filter is null)
            return null;

        BulkHabitChanges? changes;
        if (operationId == "bulk_reschedule_habits")
        {
            if (!arguments.TryGetProperty("due_date", out var dateElement)
                || dateElement.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(dateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return null;
            changes = new BulkHabitChanges(HasDueDate: true, DueDate: date);
        }
        else
        {
            var parsed = BulkHabitToolArguments.ParseChanges(arguments);
            if (parsed.Error is not null)
                return null;
            changes = parsed.Changes;
        }

        if (changes is null || !changes.HasAnyChange)
            return null;

        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter, cancellationToken);
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var rows = new List<PendingOperationChange>();
        foreach (var habit in habits.Take(10))
        {
            var update = BulkUpdateHabitsCommandHandler.ResolveUpdate(habit, changes, today);
            var preview = habit.PreviewUpdate(update);
            if (preview.IsFailure)
                return null;
            AddChanges(rows, habit, preview.Value);
        }

        return new PendingOperationChangePreview(rows, habits.Count);
    }

    private static void AddChanges(List<PendingOperationChange> rows, Habit habit, Habit effective)
    {
        void Add(string field, object? oldValue, object? newValue, string valueType, bool? changed = null)
        {
            if (changed ?? !Equals(oldValue, newValue))
                rows.Add(new PendingOperationChange(
                    habit.Id, habit.Title, field, Format(oldValue), Format(newValue), valueType));
        }

        Add("title", habit.Title, effective.Title, "text");
        Add("description", habit.Description, effective.Description, "text");
        Add("emoji", habit.Emoji, effective.Emoji, "emoji");
        Add("frequency_unit", habit.FrequencyUnit, effective.FrequencyUnit, "text");
        Add("frequency_quantity", habit.FrequencyQuantity, effective.FrequencyQuantity, "number");
        Add("interval_weeks", habit.IntervalWeeks, effective.IntervalWeeks, "number");
        Add("days", string.Join(", ", habit.Days), string.Join(", ", effective.Days), "text");
        Add("due_date", habit.DueDate, effective.DueDate, "date");
        Add("end_date", habit.EndDate, effective.EndDate, "date");
        Add("due_time", habit.DueTime, effective.DueTime, "time");
        Add("is_bad_habit", habit.IsBadHabit, effective.IsBadHabit, "boolean");
        Add("is_flexible", habit.IsFlexible, effective.IsFlexible, "boolean");
        Add("is_completed", habit.IsCompleted, effective.IsCompleted, "boolean");
        Add("reminder_enabled", habit.ReminderEnabled, effective.ReminderEnabled, "boolean");
        Add("reminder_times", FormatList(habit.ReminderTimes, offset => FormatReminderTime(habit.DueTime, offset)),
            FormatList(effective.ReminderTimes, offset => FormatReminderTime(effective.DueTime, offset)), "text",
            !habit.ReminderTimes.SequenceEqual(effective.ReminderTimes));
        Add("checklist_items", FormatList(habit.ChecklistItems, FormatChecklistItem),
            FormatList(effective.ChecklistItems, FormatChecklistItem), "text",
            !habit.ChecklistItems.SequenceEqual(effective.ChecklistItems));
        Add("scheduled_reminders", FormatList(habit.ScheduledReminders, FormatScheduledReminder),
            FormatList(effective.ScheduledReminders, FormatScheduledReminder), "text",
            !habit.ScheduledReminders.SequenceEqual(effective.ScheduledReminders));
    }

    private static string FormatList<T>(IReadOnlyList<T> values, Func<T, string> format)
    {
        var entries = values.Take(MaxDisplayedEntries)
            .Select(value => TruncateEntry(format(value)))
            .ToList();
        if (values.Count > MaxDisplayedEntries)
            entries.Add($"+{values.Count - MaxDisplayedEntries} more");
        return string.Join(", ", entries);
    }

    private static string TruncateEntry(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= MaxEntryLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, MaxEntryLength - 3), "...");
    }

    private static string FormatReminderTime(TimeOnly? dueTime, int offset)
    {
        if (dueTime is null)
            return $"{offset} min before due";

        var reminder = ReminderStoreNormalizer.ToScheduledReminder(dueTime.Value, offset);
        var time = reminder.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return reminder.When == ScheduledReminderWhen.DayBefore ? $"day_before {time}" : time;
    }

    private static string FormatScheduledReminder(ScheduledReminderTime reminder) =>
        $"{(reminder.When == ScheduledReminderWhen.DayBefore ? "day_before" : "same_day")} {reminder.Time.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    private static string FormatChecklistItem(ChecklistItem item) =>
        item.IsChecked ? $"{item.Text} (checked)" : item.Text;

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
