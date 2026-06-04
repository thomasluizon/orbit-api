using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class SentStreakFreezeAlert : Entity
{
    public Guid UserId { get; private set; }
    public DateOnly FrozenDate { get; private set; }
    public DateTime SentAtUtc { get; private set; }

    private SentStreakFreezeAlert() { }

    public static SentStreakFreezeAlert Create(Guid userId, DateOnly frozenDate)
    {
        return new SentStreakFreezeAlert
        {
            UserId = userId,
            FrozenDate = frozenDate,
            SentAtUtc = DateTime.UtcNow
        };
    }
}
