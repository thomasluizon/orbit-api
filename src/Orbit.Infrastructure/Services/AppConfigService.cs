using System.Globalization;
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

        var value = ConvertValue(key, config.Value, defaultValue);
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

    /// <summary>
    /// Parses a stored row strictly, so a typo cannot silently fall back to the default. A gate
    /// that a malformed row turns off is worse than no gate, because the runbook read-back then
    /// reports a value the server never used.
    /// </summary>
    private static T ConvertValue<T>(string key, string raw, T defaultValue)
    {
        var targetType = typeof(T);
        var trimmed = raw.Trim();

        if (targetType == typeof(string))
            return (T)(object)raw;

        if (targetType == typeof(bool))
        {
            return bool.TryParse(trimmed, out var parsed)
                ? (T)(object)parsed
                : throw MalformedRow(key, raw, "true or false");
        }

        if (targetType == typeof(int))
        {
            return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? (T)(object)parsed
                : throw MalformedRow(key, raw, "a whole number");
        }

        if (targetType == typeof(long))
        {
            return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? (T)(object)parsed
                : throw MalformedRow(key, raw, "a whole number");
        }

        if (targetType == typeof(double))
        {
            return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? (T)(object)parsed
                : throw MalformedRow(key, raw, "a decimal number");
        }

        return defaultValue;
    }

    private static InvalidOperationException MalformedRow(string key, string raw, string expected) =>
        new($"AppConfigs row '{key}' holds '{raw}', which is not {expected}. Fix the row.");
}
