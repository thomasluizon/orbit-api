using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Services;

/// <inheritdoc />
public class XpAwarder(IGenericRepository<XpAwardLog> xpAwardLogRepository) : IXpAwarder
{
    public async Task AwardAsync(
        User user,
        int amount,
        XpAwardSource source,
        Guid? sourceId,
        DateTime awardedAtUtc,
        CancellationToken cancellationToken = default)
    {
        user.AddXp(amount);
        await xpAwardLogRepository.AddAsync(
            XpAwardLog.Create(user.Id, amount, source, sourceId, awardedAtUtc), cancellationToken);
    }
}
