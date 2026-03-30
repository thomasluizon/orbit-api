using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class SentReminder : Entity
{
    public Guid HabitId { get; private set; }
    public DateOnly Date { get; private set; }
    public int MinutesBefore { get; private set; }
    public TimeOnly? ReminderTimeUtc { get; private set; }
    public DateTime SentAtUtc { get; private set; }

    private SentReminder() { }

    public static SentReminder Create(Guid habitId, DateOnly date, int minutesBefore, TimeOnly? reminderTimeUtc = null)
    {
        return new SentReminder
        {
            HabitId = habitId,
            Date = date,
            MinutesBefore = minutesBefore,
            ReminderTimeUtc = reminderTimeUtc,
            SentAtUtc = DateTime.UtcNow
        };
    }
}
