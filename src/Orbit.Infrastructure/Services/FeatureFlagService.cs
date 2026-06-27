using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class FeatureFlagService(OrbitDbContext dbContext, IMemoryCache cache) : IFeatureFlagService
{
    private const string EnabledFlagsCacheKey = "feature-flags:enabled";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<string>> GetEnabledKeysForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);

        if (user is null)
            return [];

        var flags = await GetEnabledFlagsAsync(cancellationToken);

        return flags
            .Where(flag => string.IsNullOrWhiteSpace(flag.PlanRequirement) || UserHasFeaturePlan(user, flag.PlanRequirement))
            .Select(flag => flag.Key)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<EnabledFlag>> GetEnabledFlagsAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(EnabledFlagsCacheKey, out IReadOnlyList<EnabledFlag>? cached) && cached is not null)
            return cached;

        var flags = await dbContext.AppFeatureFlags
            .AsNoTracking()
            .Where(item => item.Enabled)
            .Select(item => new EnabledFlag(item.Key, item.PlanRequirement))
            .ToListAsync(cancellationToken);

        cache.Set(EnabledFlagsCacheKey, (IReadOnlyList<EnabledFlag>)flags, CacheTtl);
        return flags;
    }

    private sealed record EnabledFlag(string Key, string? PlanRequirement);

    private static bool UserHasFeaturePlan(User user, string planRequirement)
    {
        return planRequirement.Trim().ToLowerInvariant() switch
        {
            "pro" => user.HasProAccess,
            "yearlypro" or "yearly_pro" or "yearly-pro" => user.IsYearlyPro,
            "free" => true,
            _ => false
        };
    }
}
