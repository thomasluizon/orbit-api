using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

internal static class ReminderStoreNormalizer
{
    private const int MinutesPerDay = 24 * 60;

    public static (List<int>? ReminderTimes, List<ScheduledReminderTime>? ScheduledReminders) NormalizeForUpdate(
        TimeOnly? newDueTime,
        TimeOnly? previousDueTime,
        List<int>? suppliedReminderTimes,
        List<ScheduledReminderTime>? suppliedScheduledReminders,
        IReadOnlyList<int> existingReminderTimes,
        IReadOnlyList<ScheduledReminderTime> existingScheduledReminders)
    {
        if (suppliedReminderTimes is not null || suppliedScheduledReminders is not null)
            return (suppliedReminderTimes, suppliedScheduledReminders);

        if (previousDueTime is null && newDueTime.HasValue && existingScheduledReminders.Count > 0)
            return (null, existingScheduledReminders.ToList());

        if (newDueTime is null && previousDueTime is { } removedDueTime && existingReminderTimes.Count > 0)
            return ([], ToScheduledReminders(removedDueTime, existingReminderTimes));

        return (null, null);
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
