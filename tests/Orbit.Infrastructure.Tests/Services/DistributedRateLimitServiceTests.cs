using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class DistributedRateLimitServiceTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;
    private readonly DistributedRateLimitService _service;

    public DistributedRateLimitServiceTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"DistributedRateLimitServiceTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
        _service = new DistributedRateLimitService(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TryAcquireAsync_AuthPolicy_BlocksAfterPermitLimit()
    {
        DistributedRateLimitDecision finalDecision = new(true, 0, 0, DateTime.UtcNow);

        for (var attempt = 0; attempt < 6; attempt++)
            finalDecision = await _service.TryAcquireAsync("auth", "ip:127.0.0.1");

        finalDecision.Allowed.Should().BeFalse();
        finalDecision.PermitLimit.Should().Be(5);
        finalDecision.CurrentCount.Should().Be(5);
    }

    [Fact]
    public async Task TryAcquireAsync_ChatPolicy_UsesIndependentPartitions()
    {
        for (var attempt = 0; attempt < 20; attempt++)
            (await _service.TryAcquireAsync("chat", "user:one")).Allowed.Should().BeTrue();

        var blocked = await _service.TryAcquireAsync("chat", "user:one");
        var otherPartition = await _service.TryAcquireAsync("chat", "user:two");

        blocked.Allowed.Should().BeFalse();
        blocked.PermitLimit.Should().Be(20);
        otherPartition.Allowed.Should().BeTrue();
        otherPartition.CurrentCount.Should().Be(1);
    }
}
