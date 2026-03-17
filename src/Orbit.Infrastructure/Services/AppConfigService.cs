using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class AppConfigService(OrbitDbContext dbContext, IMemoryCache cache) : IAppConfigService
{
    private const string CachePrefix = "appconfig:";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{CachePrefix}{key}";

        if (cache.TryGetValue(cacheKey, out T? cached))
            return cached!;

        var config = await dbContext.Set<AppConfig>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key, cancellationToken);

        if (config is null)
        {
            cache.Set(cacheKey, defaultValue, CacheDuration);
            return defaultValue;
        }

        var value = ConvertValue<T>(config.Value, defaultValue);
        cache.Set(cacheKey, value, CacheDuration);
        return value;
    }

    public async Task<Dictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string cacheKey = $"{CachePrefix}all";

        if (cache.TryGetValue(cacheKey, out Dictionary<string, string>? cached))
            return cached!;

        var configs = await dbContext.Set<AppConfig>()
            .AsNoTracking()
            .ToDictionaryAsync(c => c.Key, c => c.Value, cancellationToken);

        cache.Set(cacheKey, configs, CacheDuration);
        return configs;
    }

    private static T ConvertValue<T>(string raw, T defaultValue)
    {
        try
        {
            var targetType = typeof(T);

            if (targetType == typeof(int))
                return (T)(object)int.Parse(raw);
            if (targetType == typeof(bool))
                return (T)(object)bool.Parse(raw);
            if (targetType == typeof(long))
                return (T)(object)long.Parse(raw);
            if (targetType == typeof(double))
                return (T)(object)double.Parse(raw);
            if (targetType == typeof(string))
                return (T)(object)raw;

            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }
}
