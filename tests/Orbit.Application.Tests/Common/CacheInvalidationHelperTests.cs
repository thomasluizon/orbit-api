using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Common;

public class CacheInvalidationHelperTests
{
    [Fact]
    public void InvalidateSummaryCache_RemovesSixKeys()
    {
        // Arrange
        var cache = new MemoryCache(new MemoryCacheOptions());
        var userId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Populate 6 cache keys: yesterday, today, tomorrow x 2 languages
        var keys = new List<string>();
        for (int i = -1; i <= 1; i++)
        {
            var dateKey = today.AddDays(i).ToString("yyyy-MM-dd");
            var enKey = $"summary:{userId}:{dateKey}:en";
            var ptKey = $"summary:{userId}:{dateKey}:pt-BR";
            cache.Set(enKey, "cached-en");
            cache.Set(ptKey, "cached-pt");
            keys.Add(enKey);
            keys.Add(ptKey);
        }

        // Verify all 6 keys exist before invalidation
        foreach (var key in keys)
        {
            cache.TryGetValue(key, out _).Should().BeTrue($"key '{key}' should exist before invalidation");
        }

        // Act
        CacheInvalidationHelper.InvalidateSummaryCache(cache, userId);

        // Assert - all 6 keys should be removed
        foreach (var key in keys)
        {
            cache.TryGetValue(key, out _).Should().BeFalse($"key '{key}' should be removed after invalidation");
        }
    }
}
