using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class ApiKeyAuthenticationStore(OrbitDbContext db, TimeProvider clock) : IApiKeyAuthenticationStore
{
    public async Task<ApiKeyAuthenticationCandidate?> FindAsync(string prefix, CancellationToken cancellationToken)
    {
        var nowAtUtc = clock.GetUtcNow().UtcDateTime;
        var rows = await (from key in db.ApiKeys.AsNoTracking()
                          join user in db.Users.IgnoreQueryFilters() on key.UserId equals user.Id
                          where key.KeyPrefix == prefix && !key.IsRevoked
                          select new
                          {
                              Key = key,
                              user.IsDeactivated,
                              HasProAccess = user.IsLifetimePro || user.TrialEndsAt > nowAtUtc
                                  || user.BonusProExpiresAtUtc > nowAtUtc
                                  || user.SubscriptionPlan == UserPlan.Pro && user.SubscriptionExpiresAtUtc > nowAtUtc,
                              user.IsLifetimePro,
                              user.TrialEndsAt,
                              user.BonusProExpiresAtUtc,
                              user.SubscriptionExpiresAtUtc
                          }).Take(2).ToListAsync(cancellationToken);
        if (rows.Count != 1)
            return null;
        var row = rows[0];
        var endsAtUtc = row.IsLifetimePro ? (DateTime?)null :
            new[] { row.TrialEndsAt, row.BonusProExpiresAtUtc, row.SubscriptionExpiresAtUtc }
                .Where(value => value > nowAtUtc).Min();
        return new(row.Key, row.IsDeactivated, row.HasProAccess, endsAtUtc);
    }
}
