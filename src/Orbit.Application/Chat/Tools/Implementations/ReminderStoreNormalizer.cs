using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

/// <summary>
/// Routes Astra-supplied reminders into the correct one of the two mutually exclusive stores keyed on
/// whether the habit has a due time: due-timed habits use <c>reminderTimes</c> (minute offsets before the
/// due time) while habits with no due time use <c>scheduledReminders</c> (absolute day_before/same_day
/// times). The AI tools expose both shapes, so a model can emit <c>scheduled_reminders</c> for a due-timed
/// habit — the wrong store — which is why due-timed habits ended up holding scheduled reminders the client
/// never reads. This mirrors the web/mobile request builders' store selection and converts reminders across
/// the two representations so the user's intent survives both a mis-targeted tool call and a due-time
/// add/remove transition (thomasluizon/orbit-ui-mobile#447 Bug 3, PR #500).
/// </summary>
internal static class ReminderStoreNormalizer
{
    private const int MinutesPerDay = 24 * 60;

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

        var offsets = reminderTimes is not null ? new List<int>(reminderTimes) : new List<int>();

        if (scheduledReminders is not null)
        {
            foreach (var reminder in scheduledReminders)
            {
                var offset = ToMinutesBeforeDueTime(dueTime.Value, reminder);
                if (!offsets.Contains(offset))
                    offsets.Add(offset);
            }
        }

        return (offsets.Count > 0 ? offsets : reminderTimes, []);
    }

    /// <summary>
    /// Store selection for the update path, which also has to heal a due-time transition. When the caller
    /// supplies reminders this defers to <see cref="Normalize"/>. When it does not but the update adds or
    /// removes the due time, the habit's existing reminders are migrated across the boundary — absolute
    /// scheduled reminders become minute offsets before the new due time, and offsets become absolute
    /// reminders relative to the previous due time — so a due-time-only edit never strands reminders in the
    /// store the client no longer reads. Otherwise both stores are left untouched.
    /// </summary>
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
        var offsets = new List<int>();
        foreach (var reminder in reminders)
        {
            var offset = ToMinutesBeforeDueTime(dueTime, reminder);
            if (!offsets.Contains(offset))
                offsets.Add(offset);
        }
        return offsets;
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
