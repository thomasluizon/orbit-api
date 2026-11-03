using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public sealed class XpDailyTotal : Entity
{
    public Guid UserId { get; private set; }
    public DateOnly Date { get; private set; }
    public int DailyXp { get; private set; }
    public int CumulativeXp { get; private set; }
    private XpDailyTotal() { }

    public static XpDailyTotal Create(Guid userId, DateOnly date, int dailyXp, int cumulativeXp)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);
        return new() { UserId = userId, Date = date, DailyXp = dailyXp, CumulativeXp = cumulativeXp };
    }

}
