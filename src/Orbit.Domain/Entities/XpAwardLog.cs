using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

/// <summary>
/// Append-only audit row recording a single XP award. Written in the same unit of work as the
/// <c>User.TotalXp</c> mutation it accounts for, so the per-day aggregate of these rows reconstructs
/// a user's XP-over-time curve. <see cref="XpAwardSource.Reconciliation"/> rows pin a backfilled
/// curve's tail to the user's stored <c>TotalXp</c>.
/// </summary>
public class XpAwardLog : Entity
{
    public Guid UserId { get; private set; }
    public int Amount { get; private set; }
    public XpAwardSource Source { get; private set; }
    public Guid? SourceId { get; private set; }
    public DateTime AwardedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private XpAwardLog() { }

    public static XpAwardLog Create(Guid userId, int amount, XpAwardSource source, Guid? sourceId, DateTime awardedAtUtc)
    {
        return new XpAwardLog
        {
            UserId = userId,
            Amount = amount,
            Source = source,
            SourceId = sourceId,
            AwardedAtUtc = awardedAtUtc,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
