using Orbit.Domain.Enums;

namespace Orbit.Domain.ValueObjects;

public record RelativeReminderTime(
    int? MinutesBefore = null,
    ScheduledReminderWhen? When = null,
    TimeOnly? Time = null)
{
    public static RelativeReminderTime FromScheduled(ScheduledReminderTime reminder) =>
        new(When: reminder.When, Time: reminder.Time);
}
