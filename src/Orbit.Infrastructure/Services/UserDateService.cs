using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public class UserDateService(
    IGenericRepository<User> userRepository,
    IMemoryCache cache) : IUserDateService
{
    private record UserDatePreferences(string? TimeZone, int WeekStartDay);

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
        if (!cache.TryGetValue(cacheKey, out UserDatePreferences? preferences) || preferences is null)
        {
            var user = await userRepository.GetByIdAsync(userId, cancellationToken);
            preferences = new UserDatePreferences(user?.TimeZone, user?.WeekStartDay ?? 1);
            cache.Set(cacheKey, preferences, TimeSpan.FromMinutes(15));
        }

        return preferences;
    }

    public void InvalidateUserDatePreferences(Guid userId) => cache.Remove(CacheKey(userId));
}
