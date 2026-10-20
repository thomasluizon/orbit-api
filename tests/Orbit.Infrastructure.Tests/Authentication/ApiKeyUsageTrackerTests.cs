using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Authentication;

public class ApiKeyUsageTrackerTests
{
    [Fact]
    public async Task RapidCalls_OnlyUpdateTheTimestampAfterOneMinute()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var user = User.Create("Usage", "usage@example.com").Value;
        var (key, _) = ApiKey.Create(user.Id, "Usage").Value;
        factory.Context.Users.Add(user);
        factory.Context.ApiKeys.Add(key);
        await factory.Context.SaveChangesAsync();
        var time = new MutableTime();
        for (var i = 0; i < 10; i++)
        {
            using var db = factory.CreateContext();
            await new ApiKeyUsageTracker(db, time, NullLogger<ApiKeyUsageTracker>.Instance).MarkUsedAsync(key.Id);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        using var read = factory.CreateContext();
        (await read.ApiKeys.AsNoTracking().SingleAsync()).LastUsedAtUtc.Should().Be(time.Start.UtcDateTime);
        time.Advance(TimeSpan.FromMinutes(1));
        await new ApiKeyUsageTracker(read, time, NullLogger<ApiKeyUsageTracker>.Instance).MarkUsedAsync(key.Id);
        (await read.ApiKeys.AsNoTracking().SingleAsync()).LastUsedAtUtc.Should().Be(time.GetUtcNow().UtcDateTime);
    }

    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Start { get; } = DateTimeOffset.UtcNow;
        private TimeSpan _elapsed;
        public override DateTimeOffset GetUtcNow() => Start + _elapsed;
        public void Advance(TimeSpan duration) => _elapsed += duration;
    }
}
