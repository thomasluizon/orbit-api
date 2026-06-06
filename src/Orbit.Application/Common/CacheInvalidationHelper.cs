using Microsoft.Extensions.Caching.Memory;

namespace Orbit.Application.Common;

public static class CacheInvalidationHelper
{
    private static readonly string[] RetrospectivePeriods = ["week", "month", "quarter", "semester", "year"];
    private static readonly string[] SummaryTimeBuckets = ["morning", "afternoon", "evening", "night", "timeless"];

    public static void InvalidateSummaryCache(IMemoryCache cache, Guid userId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (int i = -2; i <= 2; i++)
        {
            var date = today.AddDays(i);
            foreach (var lang in AppConstants.SupportedLanguages)
            {
                cache.Remove($"summary:{userId}:{date:yyyy-MM-dd}:{lang}");
                foreach (var bucket in SummaryTimeBuckets)
                    cache.Remove($"summary:{userId}:{date:yyyy-MM-dd}:{lang}:{bucket}");
            }
        }
    }

    /// <summary>
    /// Invalidate the per-user retrospective cache. Should be called from any mutation that
    /// changes habits, logs, or goals, otherwise users see a stale 1-hour-old retrospective
    /// after logging or editing.
    /// </summary>
    public static void InvalidateRetrospectiveCache(IMemoryCache cache, Guid userId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (int i = -2; i <= 2; i++)
        {
            var date = today.AddDays(i);
            foreach (var period in RetrospectivePeriods)
                foreach (var lang in AppConstants.SupportedLanguages)
                    cache.Remove($"retro:{userId}:{period}:{date}:{lang}");
        }
    }

    /// <summary>
    /// Convenience: invalidate both summary and retrospective caches for a user. Use this from
    /// any mutation command that affects habits, logs, or goals.
    /// </summary>
    public static void InvalidateUserAiCaches(IMemoryCache cache, Guid userId)
    {
        InvalidateSummaryCache(cache, userId);
        InvalidateRetrospectiveCache(cache, userId);
    }
}
