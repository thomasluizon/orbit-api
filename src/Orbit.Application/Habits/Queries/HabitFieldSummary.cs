using System.Globalization;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Queries;

public sealed record HabitFieldSummary(
    string? Description,
    TimeOnly? DueTime,
    TimeOnly? DueEndTime,
    int? IntervalWeeks,
    IReadOnlyList<DayOfWeek> Days,
    DateOnly? EndDate,
    bool ReminderEnabled,
    IReadOnlyList<int> ReminderTimes,
    IReadOnlyList<ScheduledReminderTime> ScheduledReminders,
    IReadOnlyList<RelativeReminderTime> RelativeReminders,
    bool SlipAlertEnabled,
    bool IsFlexible,
    IReadOnlyList<ChecklistItem> ChecklistItems)
{
    public static HabitFieldSummary From(Habit habit) => new(
        habit.Description, habit.DueTime, habit.DueEndTime, habit.IntervalWeeks, habit.Days.ToList(),
        habit.EndDate, habit.ReminderEnabled, habit.ReminderTimes, habit.ScheduledReminders,
        habit.RelativeReminders, habit.SlipAlertEnabled, habit.IsFlexible, habit.ChecklistItems);

    public string Format()
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
            fields.Add($"Checklist items: {string.Join(", ", ChecklistItems.Select(i => $"[{(i.IsChecked ? "x" : " ")}] {i.Text}"))}");
        return string.Join(" | ", fields);
    }

    public static string FormatRelative(RelativeReminderTime reminder) => reminder.MinutesBefore is { } minutes
        ? $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes before"
        : FormatClock(reminder.When, reminder.Time);

    private static string FormatClock(ScheduledReminderWhen? when, TimeOnly? time) =>
        $"{(when == ScheduledReminderWhen.DayBefore ? "day_before" : "same_day")} {time?.ToString("HH:mm", CultureInfo.InvariantCulture)}";
}
