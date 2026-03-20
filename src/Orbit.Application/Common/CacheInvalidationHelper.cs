using Microsoft.Extensions.Caching.Memory;

namespace Orbit.Application.Common;

public static class CacheInvalidationHelper
{
    public static void InvalidateSummaryCache(IMemoryCache cache, Guid userId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (int i = -1; i <= 1; i++)
        {
            cache.Remove($"summary:{userId}:{today.AddDays(i):yyyy-MM-dd}:en");
            cache.Remove($"summary:{userId}:{today.AddDays(i):yyyy-MM-dd}:pt-BR");
        }
    }
}
