using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public sealed class UserCacheVersions(OrbitDbContext db) : IUserCacheVersions
{
    public async Task<long> GetAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.UserCacheVersions.AsNoTracking().Where(row => row.Id == userId)
            .Select(row => (long?)row.Version).SingleOrDefaultAsync(cancellationToken) ?? 0;

    public async Task BumpAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A cache version requires a user.");
        if (db.Database.IsRelational())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "UserCacheVersions" ("Id", "Version") VALUES ({userId}, 1)
                ON CONFLICT ("Id") DO UPDATE SET "Version" = "UserCacheVersions"."Version" + 1
                """, cancellationToken);
            return;
        }
        var row = await db.UserCacheVersions.FindAsync([userId], cancellationToken);
        if (row is null)
            db.UserCacheVersions.Add(UserCacheVersion.Create(userId));
        else
            row.Increment();
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class VersionedMemoryCache(MemoryCache cache, IUserCacheVersions versions) : IVersionedMemoryCache
{
    public IUserCacheVersions Versions { get; } = versions;
    public bool TryGetValue(object key, out object? value) => cache.TryGetValue(key, out value);
    public ICacheEntry CreateEntry(object key) => cache.CreateEntry(key);
    public void Remove(object key) => cache.Remove(key);
    public void Dispose() { }
}
