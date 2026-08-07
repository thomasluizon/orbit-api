using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Tests.Common;

public class CacheInvalidationHelperTests
{
    public static TheoryData<string, string, int> RetrospectiveKeyCases
    {
        get
        {
            var cases = new TheoryData<string, string, int>();
            foreach (var period in new[] { "week", "month", "quarter", "semester", "year" })
                foreach (var language in AppConstants.SupportedLanguages)
                    foreach (var weekStartDay in new[] { 0, 1 })
                        cases.Add(period, language, weekStartDay);

            return cases;
        }
    }

    [Fact]
    public void InvalidateSummaryCache_RemovesSummaryKeys()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var userId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var keys = new List<string>();
        for (int i = -1; i <= 1; i++)
        {
            var dateKey = today.AddDays(i).ToString("yyyy-MM-dd");
            var enKey = $"summary:{userId}:{dateKey}:en";
            var ptKey = $"summary:{userId}:{dateKey}:pt-BR";
            var eveningKey = $"summary:{userId}:{dateKey}:en:evening";
            var timelessKey = $"summary:{userId}:{dateKey}:pt-BR:timeless";
            cache.Set(enKey, "cached-en");
            cache.Set(ptKey, "cached-pt");
            cache.Set(eveningKey, "cached-evening");
            cache.Set(timelessKey, "cached-timeless");
            keys.Add(enKey);
            keys.Add(ptKey);
            keys.Add(eveningKey);
            keys.Add(timelessKey);
        }

        foreach (var key in keys)
        {
            cache.TryGetValue(key, out _).Should().BeTrue($"key '{key}' should exist before invalidation");
        }

        CacheInvalidationHelper.InvalidateSummaryCache(cache, userId, today);

        foreach (var key in keys)
        {
            cache.TryGetValue(key, out _).Should().BeFalse($"key '{key}' should be removed after invalidation");
        }
    }

    [Fact]
    public void InvalidateSummaryCache_UsesSuppliedTodayNotUtc()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var userId = Guid.NewGuid();
        var suppliedToday = new DateOnly(2020, 1, 15);
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var suppliedKey = $"summary:{userId}:{suppliedToday:yyyy-MM-dd}:en";
        var utcKey = $"summary:{userId}:{utcToday:yyyy-MM-dd}:en";
        cache.Set(suppliedKey, "cached");
        cache.Set(utcKey, "cached");

        CacheInvalidationHelper.InvalidateSummaryCache(cache, userId, suppliedToday);

        cache.TryGetValue(suppliedKey, out _).Should().BeFalse("the supplied user-local today defines the window");
        cache.TryGetValue(utcKey, out _).Should().BeTrue("an unrelated UTC-dated key stays untouched");
    }

    [Theory]
    [MemberData(nameof(RetrospectiveKeyCases))]
    public void InvalidateRetrospectiveCache_RemovesSharedKey_AndPreservesUnrelatedKeys(
        string period,
        string language,
        int weekStartDay)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var userId = Guid.NewGuid();
        var today = new DateOnly(2020, 1, 15);
        var (dateFrom, _) = RetrospectivePeriodRange.Resolve(period, today, weekStartDay);
        var targetKey = RetrospectiveCacheKey.Build(userId, period, dateFrom, language);
        var otherUserKey = RetrospectiveCacheKey.Build(Guid.NewGuid(), period, dateFrom, language);
        var otherWindowKey = RetrospectiveCacheKey.Build(userId, period, dateFrom.AddYears(-1), language);
        cache.Set(targetKey, "target");
        cache.Set(otherUserKey, "other-user");
        cache.Set(otherWindowKey, "other-window");

        CacheInvalidationHelper.InvalidateRetrospectiveCache(cache, userId, today);

        cache.TryGetValue(targetKey, out _).Should().BeFalse();
        cache.TryGetValue(otherUserKey, out _).Should().BeTrue();
        cache.TryGetValue(otherWindowKey, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("Week", "en")]
    [InlineData("week", "")]
    public void InvalidateRetrospectiveCache_RemovesAcceptedNoncanonicalKeys(
        string period,
        string language)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var userId = Guid.NewGuid();
        var today = new DateOnly(2020, 1, 15);
        var (dateFrom, _) = RetrospectivePeriodRange.Resolve(period, today, weekStartDay: 1);
        var key = RetrospectiveCacheKey.Build(userId, period, dateFrom, language);
        cache.Set(key, "target");

        CacheInvalidationHelper.InvalidateRetrospectiveCache(cache, userId, today);

        cache.TryGetValue(key, out _).Should().BeFalse();
    }
}
