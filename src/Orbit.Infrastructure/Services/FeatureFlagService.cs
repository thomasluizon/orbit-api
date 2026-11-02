using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orbit.Application.Common;
using Orbit.Application.Chat;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class FeatureFlagService(OrbitDbContext dbContext, IMemoryCache cache, IHostEnvironment environment,
    IConfiguration configuration) : IFeatureFlagService
{
    private const string FlagsCacheKey = "feature-flags:all";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public void Invalidate() => cache.Remove(FlagsCacheKey);

    public IReadOnlyList<ResolvedFeatureFlag> ResolveForUser(Guid userId)
    {
        var turn = ChatTurnReadScope.For(userId);
        if (turn?.ResolvedFlags is { } resolved) return resolved;
        var user = turn?.User ?? dbContext.Users.AsNoTracking().FirstOrDefault(item => item.Id == userId);
        if (user is null)
            return [];

        if (!cache.TryGetValue(FlagsCacheKey, out List<FlagSnapshot>? flags) || flags is null)
        {
            flags = SnapshotQuery().ToList();
            cache.Set(FlagsCacheKey, flags, CacheTtl);
        }

        var result = Resolve(user, flags);
        if (turn is not null) turn.ResolvedFlags = result;
        return result;
    }

    public async Task<IReadOnlyList<ResolvedFeatureFlag>> ResolveForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var turn = ChatTurnReadScope.For(userId);
        var user = turn?.User ?? await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
            return [];

        if (turn?.ResolvedFlags is { } resolved) return resolved;
        var result = Resolve(user, await GetSnapshotAsync(cancellationToken));
        if (turn is not null) turn.ResolvedFlags = result;
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetEnabledKeysForUsersAsync(
        IReadOnlyCollection<User> users, CancellationToken cancellationToken = default)
    {
        var flags = await GetSnapshotAsync(cancellationToken);
        return users.ToDictionary(user => user.Id,
            user => (IReadOnlyList<string>)Resolve(user, flags).Where(flag => flag.Enabled).Select(flag => flag.Key).ToList());
    }

    private async Task<List<FlagSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!forceRefresh && cache.TryGetValue(FlagsCacheKey, out List<FlagSnapshot>? cached) && cached is not null)
            return cached;

        var flags = await SnapshotQuery().ToListAsync(cancellationToken);
        cache.Set(FlagsCacheKey, flags, CacheTtl);
        return flags;
    }

    public Task<IReadOnlyList<string>> GetEnabledKeysForUserAsync(Guid userId,
        CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        var scope = ChatTurnReadScope.For(userId);
        if (!forceRefresh && scope?.EnabledFlags is { } cached) return Task.FromResult(cached);
        return !forceRefresh && scope is not null
            ? scope.ReadEnabledFlagsAsync(() => GetEnabledKeysCoreAsync(userId, cancellationToken, false))
            : GetEnabledKeysCoreAsync(userId, cancellationToken, forceRefresh);
    }

    private async Task<IReadOnlyList<string>> GetEnabledKeysCoreAsync(Guid userId, CancellationToken cancellationToken, bool forceRefresh)
    {
        var turn = forceRefresh ? null : ChatTurnReadScope.For(userId);
        var user = turn?.User ?? await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
            return [];

        if (!forceRefresh && turn?.EnabledFlags is { } enabled) return enabled;
        var resolved = !forceRefresh && turn?.ResolvedFlags is { } cached ? cached
            : Resolve(user, await GetSnapshotAsync(cancellationToken, forceRefresh));
        if (turn is not null) turn.ResolvedFlags = resolved;
        var keys = resolved.Where(flag => flag.Enabled)
            .Select(flag => flag.Key)
            .ToList();
        if (turn is not null) turn.EnabledFlags = keys;
        return keys;
    }

    private IQueryable<FlagSnapshot> SnapshotQuery() => dbContext.AppFeatureFlags
        .AsNoTracking()
        .Select(flag => new FlagSnapshot(flag.Key, flag.Enabled, flag.PlanRequirement,
            dbContext.AppFeatureFlagAccounts.Where(account => account.FlagKey == flag.Key)
                .Select(account => account.UserId).ToList()));

    private List<ResolvedFeatureFlag> Resolve(User user, IEnumerable<FlagSnapshot> flags) => flags
        .OrderBy(flag => flag.Key, StringComparer.OrdinalIgnoreCase)
        .Select(flag => new ResolvedFeatureFlag(flag.Key,
            (flag.Key != FeatureFlagKeys.AstraEvalUnmetered || environment.IsStaging()
                || (environment.IsProduction() && EvalAccountAccess.Contains(configuration[EvalAccountAccess.AccountIdsKey], user.Id)))
            && (flag.Enabled || flag.Accounts.Contains(user.Id)) && UserHasFeaturePlan(user, flag.PlanRequirement),
            flag.PlanRequirement))
        .ToList();

    private sealed record FlagSnapshot(string Key, bool Enabled, string? PlanRequirement, List<Guid> Accounts);

    private static bool UserHasFeaturePlan(User user, string? planRequirement)
    {
        if (string.IsNullOrWhiteSpace(planRequirement))
            return true;

        return planRequirement.Trim().ToLowerInvariant() switch
        {
            "pro" => user.HasProAccess,
            "free" => true,
            _ => false
        };
    }
}
