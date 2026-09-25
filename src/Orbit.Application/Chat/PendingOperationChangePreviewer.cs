using System.Globalization;
using System.Text.Json;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat;

public sealed class PendingOperationChangePreviewer(
    IGenericRepository<Habit> habitRepository) : IPendingOperationChangePreviewer
{
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
        var rows = new List<PendingOperationChange>();
        foreach (var habit in habits.Take(10))
            AddChanges(rows, habit, changes);

        return new PendingOperationChangePreview(rows, habits.Count);
    }

    private static void AddChanges(List<PendingOperationChange> rows, Habit habit, BulkHabitChanges changes)
    {
        void Add(bool specified, string field, object? oldValue, object? newValue, string valueType)
        {
            if (specified)
                rows.Add(new PendingOperationChange(
                    habit.Id, habit.Title, field, Format(oldValue), Format(newValue), valueType));
        }

        Add(changes.HasTitle, "title", habit.Title, changes.Title, "text");
        Add(changes.HasDescription, "description", habit.Description, changes.Description, "text");
        Add(changes.HasEmoji, "emoji", habit.Emoji, changes.Emoji, "emoji");
        Add(changes.HasFrequencyUnit, "frequency_unit", habit.FrequencyUnit, changes.FrequencyUnit, "text");
        Add(changes.HasFrequencyQuantity, "frequency_quantity", habit.FrequencyQuantity, changes.FrequencyQuantity, "number");
        Add(changes.HasIntervalWeeks, "interval_weeks", habit.IntervalWeeks, changes.IntervalWeeks, "number");
        Add(changes.HasDays, "days", string.Join(", ", habit.Days), string.Join(", ", changes.Days ?? []), "text");
        Add(changes.HasDueDate, "due_date", habit.DueDate, changes.DueDate, "date");
        Add(changes.HasEndDate, "end_date", habit.EndDate, changes.EndDate, "date");
        Add(changes.HasDueTime, "due_time", habit.DueTime, changes.DueTime, "time");
        Add(changes.HasIsBadHabit, "is_bad_habit", habit.IsBadHabit, changes.IsBadHabit, "boolean");
        Add(changes.HasIsFlexible, "is_flexible", habit.IsFlexible, changes.IsFlexible, "boolean");
        Add(changes.HasReminderEnabled, "reminder_enabled", habit.ReminderEnabled, changes.ReminderEnabled, "boolean");
        Add(changes.HasReminderTimes, "reminder_times", habit.ReminderTimes.Count, changes.ReminderTimes?.Count ?? 0, "count");
        Add(changes.HasChecklistItems, "checklist_items", habit.ChecklistItems.Count, changes.ChecklistItems?.Count ?? 0, "count");
        Add(changes.HasScheduledReminders, "scheduled_reminders", habit.ScheduledReminders.Count, changes.ScheduledReminders?.Count ?? 0, "count");
    }

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
