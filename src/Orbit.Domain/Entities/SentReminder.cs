using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class SentReminder : Entity
{
    public Guid HabitId { get; private set; }
    public DateOnly Date { get; private set; }
    public int MinutesBefore { get; private set; }
    public DateTime SentAtUtc { get; private set; }

    private SentReminder() { }

    public static SentReminder Create(Guid habitId, DateOnly date, int minutesBefore)
    {
        return new SentReminder
        {
            HabitId = habitId,
            Date = date,
            MinutesBefore = minutesBefore,
            SentAtUtc = DateTime.UtcNow
        };
    }
}
