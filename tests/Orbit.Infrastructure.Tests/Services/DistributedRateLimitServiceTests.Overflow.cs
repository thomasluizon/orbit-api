using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Tests.Services;

public partial class DistributedRateLimitServiceTests
{
    [Theory]
    [InlineData("support", 3, 3600, int.MaxValue - 1)]
    [InlineData("support", 3, 3600, int.MaxValue)]
    [InlineData("chat", 20, 15, int.MaxValue - 1)]
    [InlineData("chat", 20, 15, int.MaxValue)]
    public async Task TryAcquireAsync_PersistedCountsAtIntegerBoundary_DeniesWithoutOverflow(
        string policy, int limit, int segmentSeconds, int firstCount)
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 30, 30, TimeSpan.Zero));
        using var scope = new RelationalRateLimitScope(clock);
        var window = TimeSpan.FromSeconds(segmentSeconds * (policy == "chat" ? 4 : 1));
        var segment = TimeSpan.FromSeconds(segmentSeconds);
        var start = new DateTime(clock.GetUtcNow().Ticks / segment.Ticks * segment.Ticks, DateTimeKind.Utc);
        var firstStart = policy == "chat" ? start.Subtract(segment) : start;
        var first = DistributedRateLimitBucket.Create(policy, "user:overflow", firstStart, firstStart.Add(window));
        var secondStart = first.WindowStartUtc.Add(segment);
        var second = DistributedRateLimitBucket.Create(policy, "user:overflow", secondStart, secondStart.Add(window));
        scope.Context.DistributedRateLimitBuckets.AddRange(first, second);
        scope.Context.Entry(first).Property(bucket => bucket.Count).CurrentValue = firstCount;
        await scope.Context.SaveChangesAsync();
        scope.Context.ChangeTracker.Clear();

        var denied = await scope.Service.TryAcquireAsync(policy, "user:overflow");

        denied.Allowed.Should().BeFalse();
        denied.PermitLimit.Should().Be(limit);
        denied.CurrentCount.Should().Be(int.MaxValue);
        denied.WindowEndsAtUtc.Should().Be(first.WindowEndsAtUtc);
        var stored = await scope.Context.DistributedRateLimitBuckets.AsNoTracking()
            .OrderBy(bucket => bucket.WindowStartUtc).Select(bucket => bucket.Count).ToListAsync();
        stored.Should().Equal(firstCount, 1);
        (await scope.Service.TryAcquireAsync(policy, "user:other")).CurrentCount.Should().Be(1);

        clock.Advance(window.Add(segment));
        var renewed = await scope.Service.TryAcquireAsync(policy, "user:overflow");
        renewed.Allowed.Should().BeTrue();
        renewed.CurrentCount.Should().Be(1);
    }
}
