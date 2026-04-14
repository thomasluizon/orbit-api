using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class FeatureFlagService(OrbitDbContext dbContext) : IFeatureFlagService
{
    public async Task<IReadOnlyList<string>> GetEnabledKeysForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);

        if (user is null)
            return [];

        var flags = await dbContext.AppFeatureFlags
            .AsNoTracking()
            .Where(item => item.Enabled)
            .ToListAsync(cancellationToken);

        return flags
            .Where(flag => string.IsNullOrWhiteSpace(flag.PlanRequirement) || UserHasFeaturePlan(user, flag.PlanRequirement))
            .Select(flag => flag.Key)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool UserHasFeaturePlan(Orbit.Domain.Entities.User user, string planRequirement)
    {
        return planRequirement.Trim().ToLowerInvariant() switch
        {
            "pro" => user.HasProAccess,
            "yearlypro" or "yearly_pro" or "yearly-pro" => user.IsYearlyPro,
            _ => false
        };
    }
}
