using Microsoft.Extensions.Caching.Memory;

namespace Orbit.Application.Common;

public static class CacheInvalidationHelper
{
    public static void InvalidateSummaryCache(IMemoryCache cache, Guid userId)
    {
        // Use a +/-2 day buffer around UTC now to cover users in extreme offsets
        // (UTC+14 at eastern edge, UTC-12 at western edge). A 1-day buffer only covers
        // UTC+/-24h but UTC+14 means local "tomorrow" is already 2 days ahead of UTC-12.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (int i = -2; i <= 2; i++)
        {
            foreach (var lang in AppConstants.SupportedLanguages)
                cache.Remove($"summary:{userId}:{today.AddDays(i):yyyy-MM-dd}:{lang}");
        }
    }
}
