using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Domain.ValueObjects;

public static class ReminderStoreNormalizer
{
    private const int MinutesPerDay = 24 * 60;

    public static List<int> FoldScheduledReminders(
        TimeOnly dueTime,
        IReadOnlyList<int> reminderTimes,
        IReadOnlyList<ScheduledReminderTime> scheduledReminders)
    {
        var offsets = reminderTimes.Distinct().Take(Common.DomainConstants.MaxReminderTimes).ToList();
        foreach (var reminder in scheduledReminders)
        {
            var offset = ToMinutesBeforeDueTime(dueTime, reminder);
            if (!offsets.Contains(offset) && offsets.Count < Common.DomainConstants.MaxReminderTimes)
                offsets.Add(offset);
        }
        return offsets;
    }

    /// <summary>
    /// Returns the reminder stores the habit should persist. When the habit has no due time both stores
    /// pass through untouched. When it has a due time and the caller is setting reminders, every reminder
    /// is expressed as a minute offset before the due time and the scheduled-reminder store is emptied;
    /// when the caller is not touching reminders (both inputs null) neither store is modified.
    /// </summary>
    public static (List<int>? ReminderTimes, List<ScheduledReminderTime>? ScheduledReminders) Normalize(
        TimeOnly? dueTime,
        List<int>? reminderTimes,
        List<ScheduledReminderTime>? scheduledReminders)
    {
        if (dueTime is null)
            return (reminderTimes, scheduledReminders);

        if (reminderTimes is null && scheduledReminders is null)
            return (null, null);

        var offsets = FoldScheduledReminders(dueTime.Value, reminderTimes ?? [], scheduledReminders ?? []);

        return (offsets.Count > 0 ? offsets : reminderTimes, []);
    }

    public static (List<int>? ReminderTimes, List<ScheduledReminderTime>? ScheduledReminders) NormalizeForUpdate(
        TimeOnly? newDueTime,
        TimeOnly? previousDueTime,
        List<int>? suppliedReminderTimes,
        List<ScheduledReminderTime>? suppliedScheduledReminders,
        IReadOnlyList<int> existingReminderTimes,
        IReadOnlyList<ScheduledReminderTime> existingScheduledReminders)
    {
        if (suppliedReminderTimes is not null || suppliedScheduledReminders is not null)
            return Normalize(newDueTime, suppliedReminderTimes, suppliedScheduledReminders);

        if (previousDueTime is null && newDueTime is { } addedDueTime && existingScheduledReminders.Count > 0)
            return (ToOffsets(addedDueTime, existingScheduledReminders), []);

        if (newDueTime is null && previousDueTime is { } removedDueTime && existingReminderTimes.Count > 0)
            return ([], ToScheduledReminders(removedDueTime, existingReminderTimes));

        return (null, null);
    }

    private static List<int> ToOffsets(TimeOnly dueTime, IReadOnlyList<ScheduledReminderTime> reminders)
    {
        return FoldScheduledReminders(dueTime, [], reminders);
    }

    private static List<ScheduledReminderTime> ToScheduledReminders(TimeOnly dueTime, IReadOnlyList<int> offsets)
    {
        var reminders = new List<ScheduledReminderTime>();
        var seen = new HashSet<(ScheduledReminderWhen, TimeOnly)>();
        foreach (var offset in offsets)
        {
            var reminder = ToScheduledReminder(dueTime, offset);
            if (seen.Add((reminder.When, reminder.Time)))
                reminders.Add(reminder);
        }
        return reminders;
    }

    /// <summary>
    /// Minutes before the due time a scheduled reminder fires. A same-day reminder timed after the due time
    /// yields a negative raw value; it is clamped to 0 (fire at the due time) rather than dropped, so no
    /// reminder is silently lost.
    /// </summary>
    private static int ToMinutesBeforeDueTime(TimeOnly dueTime, ScheduledReminderTime reminder)
    {
        var minutesBefore = (int)(dueTime.ToTimeSpan() - reminder.Time.ToTimeSpan()).TotalMinutes;
        if (reminder.When == ScheduledReminderWhen.DayBefore)
            minutesBefore += MinutesPerDay;
        return Math.Max(minutesBefore, 0);
    }

    private static ScheduledReminderTime ToScheduledReminder(TimeOnly dueTime, int offsetMinutes)
    {
        var minutes = (int)dueTime.ToTimeSpan().TotalMinutes - offsetMinutes;
        if (minutes >= 0)
            return new ScheduledReminderTime(ScheduledReminderWhen.SameDay, MinutesToTime(minutes));

        var dayBeforeMinutes = ((minutes % MinutesPerDay) + MinutesPerDay) % MinutesPerDay;
        return new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, MinutesToTime(dayBeforeMinutes));
    }

    private static TimeOnly MinutesToTime(int minutes) =>
        TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes));
}
