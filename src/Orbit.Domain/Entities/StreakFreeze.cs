using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class StreakFreeze : Entity
{
    public Guid UserId { get; private set; }
    public DateOnly UsedOnDate { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private StreakFreeze() { }

    public static StreakFreeze Create(Guid userId, DateOnly date)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User ID is required.", nameof(userId));
        if (date == DateOnly.MinValue)
            throw new ArgumentOutOfRangeException(nameof(date), "Used date is required.");

        return new StreakFreeze
        {
            UserId = userId,
            UsedOnDate = date,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
