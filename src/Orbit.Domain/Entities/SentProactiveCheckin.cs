using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class SentProactiveCheckin : Entity
{
    public Guid UserId { get; private set; }
    public DateOnly Date { get; private set; }
    public DateTime SentAtUtc { get; private set; }

    private SentProactiveCheckin() { }

    public static SentProactiveCheckin Create(Guid userId, DateOnly date)
    {
        return new SentProactiveCheckin
        {
            UserId = userId,
            Date = date,
            SentAtUtc = DateTime.UtcNow
        };
    }
}
