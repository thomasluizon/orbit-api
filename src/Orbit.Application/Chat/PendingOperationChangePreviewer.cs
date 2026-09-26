using System.Globalization;
using System.Text.Json;
using Orbit.Domain.Common;
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
        if (operationId == "bulk_create_habits")
            return PreviewCreate(arguments);

        if (operationId is not ("bulk_update_habits" or "bulk_reschedule_habits"
            or "bulk_delete_habits" or "bulk_log_habits" or "bulk_skip_habits"
            or "bulk_update_habit_emojis" or "delete_habit"))
            return null;

        if (operationId == "delete_habit")
            return await PreviewSingleHabitAsync(userId, arguments, cancellationToken);

        var (filter, filterError) = operationId switch
        {
            "bulk_update_habits" or "bulk_reschedule_habits" => BulkHabitToolArguments.ParseRequiredFilter(arguments),
            "bulk_update_habit_emojis" => BulkHabitToolArguments.ParseEmojiFilter(arguments),
            _ => BulkHabitToolArguments.ParseActionFilter(arguments)
        };
        if (filterError is not null || filter is null)
            return null;

        BulkHabitChanges? changes = null;
        if (operationId == "bulk_reschedule_habits")
        {
            if (!arguments.TryGetProperty("due_date", out var dateElement)
                || dateElement.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(dateElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return null;
            changes = new BulkHabitChanges(HasDueDate: true, DueDate: date);
        }
        else if (operationId == "bulk_update_habits")
        {
            var parsed = BulkHabitToolArguments.ParseChanges(arguments);
            if (parsed.Error is not null)
                return null;
            changes = parsed.Changes;
        }

        if (operationId is "bulk_update_habits" or "bulk_reschedule_habits"
            && (changes is null || !changes.HasAnyChange))
            return null;

        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter, cancellationToken);
        var today = await userDateService.GetUserTodayAsync(userId, cancellationToken);
        var rows = new List<PendingOperationChange>();
        var items = new List<PendingOperationItem>();
        foreach (var habit in habits)
        {
            var fields = new List<PendingOperationChange>();
            if (changes is not null)
            {
                var update = BulkUpdateHabitsCommandHandler.ResolveUpdate(habit, changes, today);
                var preview = habit.PreviewUpdate(update);
                if (preview.IsFailure)
                    return null;
                AddChanges(fields, habit, preview.Value);
            }
            else
            {
                var field = operationId switch
                {
                    "bulk_delete_habits" => "delete",
                    "bulk_log_habits" => "log",
                    "bulk_skip_habits" => "skip",
                    _ => "emoji"
                };
                var value = arguments.TryGetProperty(field == "emoji" ? "emoji" : "date", out var proposed)
                    ? proposed.ToString() : null;
                fields.Add(new PendingOperationChange(habit.Id, habit.Title, field,
                    field == "emoji" ? habit.Emoji : null, value, field == "emoji" ? "emoji" : "action"));
            }

            items.Add(new PendingOperationItem(habit.Id.ToString(), habit.Id, habit.Title,
                fields, FingerprintHabit(habit)));
            if (items.Count <= 10)
                rows.AddRange(fields);
        }

        return BuildPreview(operationId, rows, items);
    }

    private async Task<PendingOperationChangePreview?> PreviewSingleHabitAsync(
        Guid userId, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("habit_id", out var value)
            || value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var id))
            return null;
        var filter = new BulkHabitFilter(false, [id], IncludeCompleted: true);
        var habits = await BulkHabitSelection.LoadAsync(habitRepository, userId, filter, cancellationToken);
        return BuildPreview("delete_habit", [], habits.Select(habit =>
            new PendingOperationItem(habit.Id.ToString(), habit.Id, habit.Title,
                [new PendingOperationChange(habit.Id, habit.Title, "delete", null, null, "action")],
                FingerprintHabit(habit))).ToList());
    }

    private static PendingOperationChangePreview? PreviewCreate(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("habits", out var habits) || habits.ValueKind != JsonValueKind.Array)
            return null;
        var items = habits.EnumerateArray().Select((habit, index) =>
        {
            var name = habit.ValueKind == JsonValueKind.Object
                && habit.TryGetProperty("title", out var title) ? title.ToString() : string.Empty;
            var fields = habit.ValueKind == JsonValueKind.Object
                ? habit.EnumerateObject().Select(property => new PendingOperationChange(
                    Guid.Empty, name, property.Name, null, property.Value.ToString(), "text")).ToList()
                : [];
            return new PendingOperationItem(index.ToString(CultureInfo.InvariantCulture), null,
                name, fields, AgentOperationFingerprint.Compute("bulk_create_habits", habit.GetRawText()));
        }).ToList();
        return BuildPreview("bulk_create_habits", [], items);
    }

    private static PendingOperationChangePreview BuildPreview(
        string operationId, IReadOnlyList<PendingOperationChange> changes, IReadOnlyList<PendingOperationItem> items)
    {
        var fingerprint = AgentOperationFingerprint.Compute(operationId, JsonSerializer.Serialize(items));
        return new PendingOperationChangePreview(changes, items.Count, items, fingerprint);
    }

    private static string FingerprintHabit(Habit habit) => AgentOperationFingerprint.Compute(
        habit.Id.ToString(), JsonSerializer.Serialize(new
        {
            habit.UpdatedAtUtc, habit.Title, habit.Description, habit.Emoji, habit.IsCompleted,
            habit.DueDate, habit.DueTime, habit.EndDate, habit.FrequencyUnit,
            habit.FrequencyQuantity, habit.IntervalWeeks, habit.IsBadHabit, habit.IsFlexible,
            habit.ReminderEnabled, habit.ReminderTimes, habit.Days, habit.ChecklistItems,
            habit.ScheduledReminders, habit.IsDeleted
        }));

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
        AddList("reminder_times", habit.ReminderTimes, effective.ReminderTimes, FormatReminderTime);
        AddList("checklist_items", habit.ChecklistItems, effective.ChecklistItems, FormatChecklistItem);
        AddList("scheduled_reminders", habit.ScheduledReminders, effective.ScheduledReminders, FormatScheduledReminder);

        void AddList<T>(string field, IReadOnlyList<T> oldValues, IReadOnlyList<T> newValues, Func<T, string> format)
        {
            if (oldValues.SequenceEqual(newValues))
                return;

            var (oldText, newText) = FormatChangedList(oldValues, newValues, format);
            Add(field, oldText, newText, "text", changed: true);
        }
    }

    private static (string OldText, string NewText) FormatChangedList<T>(
        IReadOnlyList<T> oldValues, IReadOnlyList<T> newValues, Func<T, string> format)
    {
        var firstDifference = 0;
        while (firstDifference < Math.Min(oldValues.Count, newValues.Count)
            && EqualityComparer<T>.Default.Equals(oldValues[firstDifference], newValues[firstDifference]))
            firstDifference++;

        var oldText = FormatListWindow(oldValues, firstDifference, format);
        var newText = FormatListWindow(newValues, firstDifference, format);
        if (oldText == newText)
            newText += " (changed)";
        return (oldText, newText);
    }

    private static string FormatListWindow<T>(IReadOnlyList<T> values, int start, Func<T, string> format)
    {
        var entries = new List<string>();
        if (start > 0)
            entries.Add($"+{start} earlier");

        var displayed = values.Skip(start).Take(MaxDisplayedEntries)
            .Select(value => TruncateEntry(format(value)))
            .ToList();
        if (displayed.Count == 0)
            entries.Add("(none)");
        else
            entries.AddRange(displayed);

        var remaining = values.Count - start - displayed.Count;
        if (remaining > 0)
            entries.Add($"+{remaining} more");
        return string.Join(", ", entries);
    }

    private static string TruncateEntry(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= MaxEntryLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, MaxEntryLength - 3), "...");
    }

    private static string FormatReminderTime(int offset) => $"{offset} min before due";

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
