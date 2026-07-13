using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public class UserDateService(
    IGenericRepository<User> userRepository,
    IDistributedCache cache) : IUserDateService
{
    private static readonly DistributedCacheEntryOptions CacheEntryOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15)
    };

    private sealed record UserDatePreferences(string? TimeZone, int WeekStartDay);

    private static string CacheKey(Guid userId) => $"user-tz:{userId}";

    public async Task<DateOnly> GetUserTodayAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var preferences = await GetPreferencesAsync(userId, cancellationToken);
        var timeZone = TimeZoneHelper.FindTimeZone(preferences.TimeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
    }

    public async Task<int> GetUserWeekStartDayAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var preferences = await GetPreferencesAsync(userId, cancellationToken);
        return preferences.WeekStartDay;
    }

    private async Task<UserDatePreferences> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var cacheKey = CacheKey(userId);
        var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            var deserialized = JsonSerializer.Deserialize<UserDatePreferences>(cached);
            if (deserialized is not null)
                return deserialized;
        }

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        var preferences = new UserDatePreferences(user?.TimeZone, user?.WeekStartDay ?? 1);
        await cache.SetStringAsync(
            cacheKey, JsonSerializer.Serialize(preferences), CacheEntryOptions, cancellationToken);

        return preferences;
    }

    public Task InvalidateUserDatePreferencesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(CacheKey(userId), cancellationToken);
}
