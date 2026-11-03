using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class AuthValidationCache(MemoryCache cache, IOptions<AuthCacheSettings> settings, TimeProvider clock)
{
    private readonly object _gate = new();
    private long _epoch;
    public long Epoch => Interlocked.Read(ref _epoch);

    private Guid Version(Guid userId)
    {
        var key = ("auth-user-version", userId);
        if (cache.TryGetValue(key, out Guid version)) return version;
        version = Guid.NewGuid();
        cache.Set(key, version, VersionLifetime);
        return version;
    }

    private TimeSpan VersionLifetime => TimeSpan.FromSeconds(
        Math.Max(settings.Value.ApiKeyTtlSeconds, settings.Value.SessionTtlSeconds) + 60);

    public void InvalidateUser(Guid userId)
    {
        lock (_gate)
        {
            cache.Set(("auth-user-version", userId), Guid.NewGuid(), VersionLifetime);
            Interlocked.Increment(ref _epoch);
        }
    }

    public bool TryGet<T>(object key, out T? value) where T : class
    {
        lock (_gate)
        {
            if (cache.TryGetValue(key, out Entry<T>? entry) && entry is not null && entry.Version == Version(entry.UserId))
            {
                value = entry.Value;
                return true;
            }
            value = null;
            return false;
        }
    }

    public void Set<T>(object key, Guid userId, T value, long observedEpoch, bool session, DateTime? expiresAtUtc = null) where T : class
    {
        var ttlSeconds = session ? settings.Value.SessionTtlSeconds : settings.Value.ApiKeyTtlSeconds;
        var nowAtUtc = clock.GetUtcNow().UtcDateTime;
        if (ttlSeconds <= 0) return;
        var expiryAtUtc = nowAtUtc.AddSeconds(ttlSeconds);
        if (expiresAtUtc < expiryAtUtc) expiryAtUtc = expiresAtUtc.Value;
        if (expiryAtUtc <= nowAtUtc) return;
        lock (_gate)
        {
            if (observedEpoch == _epoch)
                cache.Set(key, new Entry<T>(userId, Version(userId), value), new DateTimeOffset(expiryAtUtc));
        }
    }

    private sealed record Entry<T>(Guid UserId, Guid Version, T Value);
}
