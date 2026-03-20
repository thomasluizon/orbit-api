using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class SentSlipAlert : Entity
{
    public Guid HabitId { get; private set; }
    public DateOnly WeekStart { get; private set; }
    public DateTime SentAtUtc { get; private set; }

    private SentSlipAlert() { }

    public static SentSlipAlert Create(Guid habitId, DateOnly weekStart)
    {
        return new SentSlipAlert
        {
            HabitId = habitId,
            WeekStart = weekStart,
            SentAtUtc = DateTime.UtcNow
        };
    }
}
