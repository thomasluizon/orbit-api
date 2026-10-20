using System.Globalization;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Queries;

public sealed record HabitFieldSummary
{
    public string? Description { get; init; }
    public TimeOnly? DueTime { get; init; }
    public TimeOnly? DueEndTime { get; init; }
    public int? IntervalWeeks { get; init; }
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];
    public DateOnly? EndDate { get; init; }
    public bool ReminderEnabled { get; init; }
    public IReadOnlyList<int> ReminderTimes { get; init; } = [];
    public IReadOnlyList<ScheduledReminderTime> ScheduledReminders { get; init; } = [];
    public IReadOnlyList<RelativeReminderTime> RelativeReminders { get; init; } = [];
    public bool SlipAlertEnabled { get; init; }
    public bool IsFlexible { get; init; }
    public IReadOnlyList<ChecklistItem> ChecklistItems { get; init; } = [];

    public static HabitFieldSummary From(Habit habit) => new()
    {
        Description = habit.Description,
        DueTime = habit.DueTime,
        DueEndTime = habit.DueEndTime,
        IntervalWeeks = habit.IntervalWeeks,
        Days = habit.Days.ToList(),
        EndDate = habit.EndDate,
        ReminderEnabled = habit.ReminderEnabled,
        ReminderTimes = habit.ReminderTimes,
        ScheduledReminders = habit.ScheduledReminders,
        RelativeReminders = habit.RelativeReminders,
        SlipAlertEnabled = habit.SlipAlertEnabled,
        IsFlexible = habit.IsFlexible,
        ChecklistItems = habit.ChecklistItems
    };

    public string Format(DateOnly? dueDate = null)
    {
        var fields = new List<string>();
        if (!string.IsNullOrWhiteSpace(Description)) fields.Add($"Description: {Description}");
        if (DueTime is { } time) fields.Add($"Due time: {time.ToString("HH:mm", CultureInfo.InvariantCulture)}");
        if (DueEndTime is { } endTime) fields.Add($"Due end time: {endTime.ToString("HH:mm", CultureInfo.InvariantCulture)}");
        if (IntervalWeeks is { } weeks) fields.Add($"Interval weeks: {weeks.ToString(CultureInfo.InvariantCulture)}");
        if (Days.Count > 0) fields.Add($"Days: {string.Join(", ", Days)}");
        if (EndDate is { } endDate) fields.Add($"End date: {endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        if (ReminderEnabled) fields.Add("Reminders: enabled");
        if (ReminderTimes.Count > 0) fields.Add($"Reminder minutes: {string.Join(", ", ReminderTimes)}");
        if (ScheduledReminders.Count > 0)
            fields.Add($"Scheduled reminders: {string.Join(", ", ScheduledReminders.Select(r => FormatClock(r.When, r.Time)))}");
        if (RelativeReminders.Count > 0)
            fields.Add($"Relative reminders: {string.Join(", ", RelativeReminders.Select(FormatRelative))}");
        if (SlipAlertEnabled) fields.Add("Slip alerts: enabled");
        if (IsFlexible) fields.Add("Flexible: true");
        if (ChecklistItems.Count > 0)
            fields.Add($"Checklist items: {string.Join(", ", ChecklistItems.Select(FormatChecklistItem))}");
        if (dueDate is { } due) fields.Add($"Due: {due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        return string.Join(" | ", fields);
    }

    private static string FormatChecklistItem(ChecklistItem item) => $"[{(item.IsChecked ? "x" : " ")}] {item.Text}";

    public static string FormatRelative(RelativeReminderTime reminder) => reminder.MinutesBefore is { } minutes
        ? $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes before"
        : FormatClock(reminder.When, reminder.Time);

    private static string FormatClock(ScheduledReminderWhen? when, TimeOnly? time) =>
        $"{(when == ScheduledReminderWhen.DayBefore ? "day_before" : "same_day")} {time?.ToString("HH:mm", CultureInfo.InvariantCulture)}";
}
