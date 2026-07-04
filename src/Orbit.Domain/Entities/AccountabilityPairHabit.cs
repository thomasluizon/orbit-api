using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class AccountabilityPairHabit : Entity
{
    public Guid PairId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid HabitId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private AccountabilityPairHabit() { }

    public static AccountabilityPairHabit Create(Guid pairId, Guid userId, Guid habitId)
    {
        return new AccountabilityPairHabit
        {
            PairId = pairId,
            UserId = userId,
            HabitId = habitId,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
