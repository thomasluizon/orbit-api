using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Common;

public class CacheInvalidationHelperTests
{
    [Fact]
    public void InvalidateSummaryCache_RemovesSummaryKeys()
    {
        // Arrange
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

        // Act
        CacheInvalidationHelper.InvalidateSummaryCache(cache, userId);

        foreach (var key in keys)
        {
            cache.TryGetValue(key, out _).Should().BeFalse($"key '{key}' should be removed after invalidation");
        }
    }
}
