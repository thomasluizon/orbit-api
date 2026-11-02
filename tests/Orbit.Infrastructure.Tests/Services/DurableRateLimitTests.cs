using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Domain.Entities;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class DurableRateLimitTests
{
    [Fact]
    public async Task OneHundredAllowedRequests_RunNoCleanupAndThePeriodicJobRemovesExpiredState()
    {
        var statements = new StatementCounter();
        using var factory = new SqliteOrbitDbContextFactory(statements);
        using var db = factory.CreateContext();
        var nowAtUtc = DateTime.UtcNow;
        var expired = DistributedRateLimitBucket.Create("auth", "expired", nowAtUtc.AddMinutes(-2), nowAtUtc.AddMinutes(-1));
        var expiredLease = DistributedRateLimitBucket.Create("chat-in-flight", "expired", DateTime.UnixEpoch, nowAtUtc.AddMinutes(-1));
        db.AddRange(expired, expiredLease);
        await db.SaveChangesAsync();
        statements.Deletes = 0;
        var service = new DistributedRateLimitService(db, TimeProvider.System);
        for (var i = 0; i < 100; i++)
            (await service.TryAcquireAsync("auth", $"user:{i}")).Allowed.Should().BeTrue();
        statements.Deletes.Should().Be(0);
        (await db.DistributedRateLimitBuckets.CountAsync()).Should().Be(102);
        using var provider = new ServiceCollection().AddScoped(_ => factory.CreateContext()).BuildServiceProvider();
        await new AuthStateCleanupService(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<AuthStateCleanupService>.Instance).RunAsync(default);
        (await db.DistributedRateLimitBuckets.CountAsync()).Should().Be(100);
        (await db.DistributedRateLimitBuckets.AnyAsync(row => row.Id == expired.Id || row.Id == expiredLease.Id)).Should().BeFalse();
        statements.Deletes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RateLimitAndLease_AcquireInOneTransactionAndPreserveBothLimits()
    {
        var statements = new StatementCounter();
        using var factory = new SqliteOrbitDbContextFactory(statements);
        using var db = factory.CreateContext();
        var service = new DistributedRateLimitService(db, TimeProvider.System);
        statements.Starts = 0;
        var first = await service.TryAcquireWithLeaseAsync("chat", "user:one", "chat-in-flight", 1, TimeSpan.FromMinutes(5));
        first.RateLimit.Allowed.Should().BeTrue();
        first.Lease!.Allowed.Should().BeTrue();
        statements.Starts.Should().Be(1);
        var repeated = await service.TryAcquireWithLeaseAsync("chat", "user:one", "chat-in-flight", 1, TimeSpan.FromMinutes(5));
        repeated.RateLimit.Allowed.Should().BeTrue();
        repeated.Lease!.Allowed.Should().BeFalse();
        repeated.RateLimit.CurrentCount.Should().Be(2);
        await service.ReleaseLeaseAsync("chat-in-flight", "user:one", first.Lease.WindowEndsAtUtc);
        var next = await service.TryAcquireWithLeaseAsync("chat", "user:one", "chat-in-flight", 1, TimeSpan.FromMinutes(5));
        next.Lease!.Allowed.Should().BeTrue();
        statements.Deletes.Should().Be(1, "only the explicit lease release deletes on the request path");
    }

    private sealed class StatementCounter : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public int Deletes;
        public int Starts;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) Deletes++;
            return ValueTask.FromResult(result);
        }
        public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            Starts++;
            return ValueTask.FromResult(result);
        }
    }
}
