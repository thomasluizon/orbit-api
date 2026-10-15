using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Calendar;

namespace Orbit.Infrastructure.Tests.Services;

public class CalendarSyncLockTests
{
    [Fact]
    public async Task TryAcquireAsync_WithoutPostgres_DoesNotIssueProviderSql()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new OrbitDbContext(options);

        var acquired = await new CalendarSyncLock(context).TryAcquireAsync(Guid.NewGuid(), CancellationToken.None);

        acquired.Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquireAsync_PostgresWithoutTransaction_RejectsUnprotectedLock()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        await using var context = new OrbitDbContext(options);

        var acquire = () => new CalendarSyncLock(context).TryAcquireAsync(Guid.NewGuid(), CancellationToken.None);

        await acquire.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Calendar sync requires an active transaction.");
    }
}
