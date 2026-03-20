using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class SentReminder : Entity
{
    public Guid HabitId { get; private set; }
    public DateOnly Date { get; private set; }
    public DateTime SentAtUtc { get; private set; }

    private SentReminder() { }

    public static SentReminder Create(Guid habitId, DateOnly date)
    {
        return new SentReminder
        {
            HabitId = habitId,
            Date = date,
            SentAtUtc = DateTime.UtcNow
        };
    }
}
