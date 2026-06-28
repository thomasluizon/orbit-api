using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// The single audited XP-award funnel. Mutates <see cref="User.TotalXp"/> and appends a matching
/// <see cref="XpAwardLog"/> row in the same unit of work (persisted by the caller's SaveChanges), so
/// the per-day aggregate of those rows reconstructs the user's XP-over-time curve. <c>User.AddXp</c>
/// is called only from the implementation of this interface.
/// </summary>
public interface IXpAwarder
{
    Task AwardAsync(
        User user,
        int amount,
        XpAwardSource source,
        Guid? sourceId,
        DateTime awardedAtUtc,
        CancellationToken cancellationToken = default);
}
