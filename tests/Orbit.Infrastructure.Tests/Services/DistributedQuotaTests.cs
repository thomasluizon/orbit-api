using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class DistributedQuotaTests
{
    [Fact]
    public async Task Reservations_NeverExceedBudget_AndPersistOnlyBuckets()
    {
        await using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new DistributedRateLimitService(db, TimeProvider.System);
        (await service.TryAcquireAsync("onboarding-tokens", "global", 10, 6, TimeSpan.FromDays(1), default)).Should().BeTrue();
        (await service.TryAcquireAsync("onboarding-tokens", "global", 10, 6, TimeSpan.FromDays(1), default)).Should().BeFalse();
        db.DistributedRateLimitBuckets.Single().Count.Should().Be(6);
        db.ChangeTracker.Entries().Should().OnlyContain(entry => entry.Entity is Orbit.Domain.Entities.DistributedRateLimitBucket);
    }

    [Fact]
    public async Task MissingOrInsufficientBudget_WritesNothing()
    {
        await using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var service = new DistributedRateLimitService(db, TimeProvider.System);
        (await service.TryAcquireAsync("onboarding-tokens", "global", 0, 1, TimeSpan.FromDays(1), default)).Should().BeFalse();
        db.ChangeTracker.Entries().Should().BeEmpty();
    }
}
