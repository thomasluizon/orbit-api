using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Services;

public class UserDateService(
    IGenericRepository<User> userRepository,
    IMemoryCache cache) : IUserDateService
{
    private static string CacheKey(Guid userId) => $"user-tz:{userId}";

    public async Task<DateOnly> GetUserTodayAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKey(userId);
        if (!cache.TryGetValue(cacheKey, out string? tzId))
        {
            var user = await userRepository.GetByIdAsync(userId, cancellationToken);
            tzId = user?.TimeZone;
            cache.Set(cacheKey, tzId, TimeSpan.FromMinutes(15));
        }

        var timeZone = TimeZoneHelper.FindTimeZone(tzId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
    }

    public void InvalidateUserTimezone(Guid userId) => cache.Remove(CacheKey(userId));
}
