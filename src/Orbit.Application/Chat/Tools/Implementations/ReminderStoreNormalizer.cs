using Orbit.Domain.Enums;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Chat.Tools.Implementations;

/// <summary>
/// Routes Astra-supplied reminders into the correct one of the two mutually exclusive stores keyed on
/// whether the habit has a due time: due-timed habits use <c>reminderTimes</c> (minute offsets before the
/// due time) while habits with no due time use <c>scheduledReminders</c> (absolute day_before/same_day
/// times). The AI tools expose both shapes, so a model can emit <c>scheduled_reminders</c> for a due-timed
/// habit — the wrong store — which is why due-timed habits ended up holding scheduled reminders the client
/// never reads. This mirrors the web/mobile request builders' store selection and converts any absolute
/// reminders on a due-timed habit into offsets so the intent survives
/// (thomasluizon/orbit-ui-mobile#447 Bug 3, PR #500).
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
                if (offset >= 0 && !offsets.Contains(offset))
                    offsets.Add(offset);
            }
        }

        return (offsets.Count > 0 ? offsets : reminderTimes, []);
    }

    private static int ToMinutesBeforeDueTime(TimeOnly dueTime, ScheduledReminderTime reminder)
    {
        var minutesBefore = (int)(dueTime.ToTimeSpan() - reminder.Time.ToTimeSpan()).TotalMinutes;
        return reminder.When == ScheduledReminderWhen.DayBefore ? minutesBefore + MinutesPerDay : minutesBefore;
    }
}
