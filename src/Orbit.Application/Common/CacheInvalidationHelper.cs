using Microsoft.Extensions.Caching.Memory;

namespace Orbit.Application.Common;

public static class CacheInvalidationHelper
{
    private static readonly string[] RetrospectivePeriods = ["week", "month", "quarter", "semester", "year"];

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

    /// <summary>
    /// Invalidate the per-user retrospective cache. Should be called from any mutation that
    /// changes habits, logs, or goals, otherwise users see a stale 1-hour-old retrospective
    /// after logging or editing.
    /// </summary>
    public static void InvalidateRetrospectiveCache(IMemoryCache cache, Guid userId)
    {
        // The cache key is keyed on (userId, period, dateFrom, language). dateFrom varies by
        // when the user requested it, so we cannot enumerate exact keys. Best-effort: clear
        // each (period, language) pair for the most common dateFrom values (today and the
        // first-of-period for the current calendar period). For now, since IMemoryCache has
        // no prefix-scan, we burn keys for the +/-2 day window (matches summary semantics).
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
