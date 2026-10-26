using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class DistributedRateLimitService(OrbitDbContext dbContext, TimeProvider clock)
    : IDistributedRateLimitService, IDistributedQuotaService
{
    private static readonly DateTime LeaseWindowStartUtc = DateTime.UnixEpoch;

    private static readonly Dictionary<string, RateLimitPolicy> Policies =
        new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth"] = new(TimeSpan.FromMinutes(1), PermitLimit: 10, SegmentCount: 1),
            ["auth-ip"] = new(TimeSpan.FromMinutes(1), PermitLimit: 30, SegmentCount: 1),
            ["refresh"] = new(TimeSpan.FromMinutes(1), PermitLimit: 10, SegmentCount: 1),
            ["waitlist"] = new(TimeSpan.FromMinutes(10), PermitLimit: 5, SegmentCount: 1),
            ["waitlist-ip"] = new(TimeSpan.FromMinutes(10), PermitLimit: 15, SegmentCount: 1),
            ["chat"] = new(TimeSpan.FromMinutes(1), PermitLimit: 20, SegmentCount: 4),
            ["ai-resolve"] = new(TimeSpan.FromMinutes(1), PermitLimit: 30, SegmentCount: 4),
            ["ai-operations"] = new(TimeSpan.FromMinutes(1), PermitLimit: 15, SegmentCount: 4),
            ["habit-suggest"] = new(TimeSpan.FromMinutes(1), PermitLimit: 15, SegmentCount: 4),
            ["support"] = new(TimeSpan.FromHours(1), PermitLimit: 3, SegmentCount: 1),
            ["uploads"] = new(TimeSpan.FromMinutes(1), PermitLimit: 30, SegmentCount: 4),
            ["tag-suggest"] = new(TimeSpan.FromMinutes(1), PermitLimit: 15, SegmentCount: 4),
            ["tags"] = new(TimeSpan.FromMinutes(1), PermitLimit: 60, SegmentCount: 4),
            ["achievements"] = new(TimeSpan.FromMinutes(1), PermitLimit: 30, SegmentCount: 4),
            ["streak-repair"] = new(TimeSpan.FromMinutes(1), PermitLimit: 10, SegmentCount: 1),
            ["admin-broadcast"] = new(TimeSpan.FromHours(1), PermitLimit: 5, SegmentCount: 1),
            ["marketing-unsubscribe"] = new(TimeSpan.FromMinutes(1), PermitLimit: 20, SegmentCount: 4),
            ["ses-events"] = new(TimeSpan.FromMinutes(1), PermitLimit: 600, SegmentCount: 4),
            ["mcp"] = new(TimeSpan.FromMinutes(1), PermitLimit: 60, SegmentCount: 4),
            ["mcp-ai"] = new(TimeSpan.FromMinutes(1), PermitLimit: 15, SegmentCount: 4)
        };

    public async Task<DistributedRateLimitDecision> TryAcquireAsync(
        string policyName,
        string partitionKey,
        CancellationToken cancellationToken = default)
    {
        if (!Policies.TryGetValue(policyName, out var policy))
            throw new InvalidOperationException($"Unknown rate-limit policy '{policyName}'.");

        cancellationToken.ThrowIfCancellationRequested();

        var useRelationalTransactionPath =
            dbContext.Database.IsRelational() &&
            dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) != true;

        if (!useRelationalTransactionPath)
            return await TryAcquireCoreAsync(policyName, partitionKey, policy, cancellationToken);

        if (dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Rate-limit acquisition requires a context without pending changes.");

        return await ExecuteRelationalAttemptAsync(policyName, partitionKey, policy, cancellationToken);
    }

    public async Task<bool> TryAcquireAsync(string policy, string partition, int limit, int amount,
        TimeSpan window, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, 1);
        if (limit < amount || window <= TimeSpan.Zero)
            return false;

        var quota = new RateLimitPolicy(window, limit, 1, amount);
        if (dbContext.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Quota acquisition requires a context without pending changes.");
        var result = dbContext.Database.IsRelational()
            ? await ExecuteRelationalAttemptAsync(policy, partition, quota, cancellationToken)
            : await TryAcquireCoreAsync(policy, partition, quota, cancellationToken);
        return result.Allowed;
    }

    public async Task<DistributedRateLimitDecision> TryAcquireLeaseAsync(
        string policyName,
        string partitionKey,
        int permitLimit,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        var now = clock.GetUtcNow().UtcDateTime;
        var leaseEndsAtUtc = now.Add(leaseDuration);
        await DeleteExpiredLeasesAsync(policyName, now, cancellationToken);

        var existingLease = await dbContext.DistributedRateLimitBuckets
            .AsNoTracking()
            .SingleOrDefaultAsync(
                bucket => bucket.PolicyName == policyName &&
                          bucket.PartitionKey == partitionKey &&
                          bucket.WindowStartUtc == LeaseWindowStartUtc,
                cancellationToken);

        if (existingLease is not null)
        {
            return new DistributedRateLimitDecision(
                false,
                permitLimit,
                1,
                existingLease.WindowEndsAtUtc);
        }

        dbContext.DistributedRateLimitBuckets.Add(DistributedRateLimitBucket.Create(
            policyName,
            partitionKey,
            LeaseWindowStartUtc,
            leaseEndsAtUtc));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new DistributedRateLimitDecision(true, permitLimit, 1, leaseEndsAtUtc);
        }
        catch (DbUpdateException exception) when (IsLeaseUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            var winningLeaseEndsAtUtc = await dbContext.DistributedRateLimitBuckets
                .AsNoTracking()
                .Where(bucket => bucket.PolicyName == policyName &&
                                 bucket.PartitionKey == partitionKey &&
                                 bucket.WindowStartUtc == LeaseWindowStartUtc)
                .Select(bucket => (DateTime?)bucket.WindowEndsAtUtc)
                .SingleOrDefaultAsync(cancellationToken);

            return new DistributedRateLimitDecision(
                false,
                permitLimit,
                1,
                winningLeaseEndsAtUtc ?? leaseEndsAtUtc);
        }
    }

    public async Task ReleaseLeaseAsync(
        string policyName,
        string partitionKey,
        DateTime leaseEndsAtUtc,
        CancellationToken cancellationToken = default)
    {
        await dbContext.DistributedRateLimitBuckets
            .Where(bucket => bucket.PolicyName == policyName &&
                             bucket.PartitionKey == partitionKey &&
                             bucket.WindowStartUtc == LeaseWindowStartUtc &&
                             bucket.WindowEndsAtUtc == leaseEndsAtUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task DeleteExpiredLeasesAsync(
        string policyName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var expiredLeases = dbContext.DistributedRateLimitBuckets
            .Where(bucket => bucket.PolicyName == policyName &&
                             bucket.WindowStartUtc == LeaseWindowStartUtc &&
                             bucket.WindowEndsAtUtc <= now);

        if (dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
        {
            var expiredEntities = await expiredLeases.ToListAsync(cancellationToken);
            if (expiredEntities.Count > 0)
            {
                dbContext.DistributedRateLimitBuckets.RemoveRange(expiredEntities);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        await expiredLeases.ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<DistributedRateLimitDecision> ExecuteRelationalAttemptAsync(
        string policyName,
        string partitionKey,
        RateLimitPolicy policy,
        CancellationToken cancellationToken)
    {
        var strategy = CreateAcquisitionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            strategy.CommitStarted = false;
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                token);

            try
            {
                await AcquireTransactionLockAsync(token);
                var decision = await TryAcquireCoreAsync(policyName, partitionKey, policy, token);
                strategy.CommitStarted = true;
                await transaction.CommitAsync(token);
                return decision;
            }
            catch
            {
                foreach (var entry in dbContext.ChangeTracker.Entries<DistributedRateLimitBucket>().ToList())
                    entry.State = EntityState.Detached;
                throw;
            }
        }, cancellationToken);
    }

    internal virtual Task AcquireTransactionLockAsync(CancellationToken cancellationToken)
        => dbContext.Database.IsNpgsql()
            // https://www.postgresql.org/docs/17/sql-lock.html: lock before the first query establishes the Serializable snapshot.
            ? dbContext.Database.ExecuteSqlRawAsync(
                "LOCK TABLE \"DistributedRateLimitBuckets\" IN SHARE ROW EXCLUSIVE MODE", cancellationToken)
            : Task.CompletedTask;

    private async Task<DistributedRateLimitDecision> TryAcquireCoreAsync(
        string policyName,
        string partitionKey,
        RateLimitPolicy policy,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var segmentWindow = TimeSpan.FromTicks(policy.Window.Ticks / policy.SegmentCount);
        var segmentStartUtc = FloorUtc(now, segmentWindow);
        var bucketExpiresAtUtc = segmentStartUtc.Add(policy.Window);
        var activeWindowStartUtc = policy.SegmentCount == 1
            ? segmentStartUtc
            : now - policy.Window + segmentWindow;

        var expiredBuckets = dbContext.DistributedRateLimitBuckets
            .Where(bucket => bucket.PolicyName == policyName && bucket.WindowEndsAtUtc <= now);

        if (dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
        {
            var expiredEntities = await expiredBuckets.ToListAsync(cancellationToken);
            if (expiredEntities.Count > 0)
            {
                dbContext.DistributedRateLimitBuckets.RemoveRange(expiredEntities);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        else
        {
            await expiredBuckets.ExecuteDeleteAsync(cancellationToken);
        }

        var recentBuckets = await dbContext.DistributedRateLimitBuckets
            .Where(bucket =>
                bucket.PolicyName == policyName &&
                bucket.PartitionKey == partitionKey &&
                bucket.WindowStartUtc >= activeWindowStartUtc)
            .OrderBy(bucket => bucket.WindowStartUtc)
            .ToListAsync(cancellationToken);

        var currentCount = recentBuckets.Sum(bucket => (long)bucket.Count);
        if (currentCount > (long)policy.PermitLimit - policy.Amount)
        {
            var oldestRelevantBucket = recentBuckets.FirstOrDefault();
            return new DistributedRateLimitDecision(
                false,
                policy.PermitLimit,
                (int)Math.Min(currentCount, int.MaxValue),
                oldestRelevantBucket?.WindowEndsAtUtc ?? bucketExpiresAtUtc);
        }

        var currentBucket = recentBuckets.FirstOrDefault(bucket => bucket.WindowStartUtc == segmentStartUtc);
        if (currentBucket is null)
        {
            dbContext.DistributedRateLimitBuckets.Add(DistributedRateLimitBucket.Create(
                policyName,
                partitionKey,
                segmentStartUtc,
                bucketExpiresAtUtc,
                policy.Amount));
        }
        else
        {
            currentBucket.Increment(policy.Amount);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new DistributedRateLimitDecision(
            true,
            policy.PermitLimit,
            (int)currentCount + policy.Amount,
            bucketExpiresAtUtc);
    }

    internal virtual AcquisitionExecutionStrategy CreateAcquisitionStrategy() => new(dbContext);

    internal class AcquisitionExecutionStrategy(OrbitDbContext context)
        : NpgsqlRetryingExecutionStrategy(context, 2, TimeSpan.FromSeconds(5), [PostgresErrorCodes.UniqueViolation])
    {
        public bool CommitStarted { get; set; }

        protected override bool ShouldRetryOn(Exception? exception)
            => CommitStarted
                ? exception is PostgresException
                {
                    SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
                        or PostgresErrorCodes.UniqueViolation
                }
                : base.ShouldRetryOn(exception);
    }

    private static bool IsLeaseUniqueViolation(Exception exception)
    {
        return exception switch
        {
            DbUpdateException { InnerException: { } innerException } =>
                IsLeaseUniqueViolation(innerException),
            PostgresException postgresException => postgresException.SqlState == PostgresErrorCodes.UniqueViolation,
            _ => false
        };
    }

    private static DateTime FloorUtc(DateTime utcNow, TimeSpan window)
    {
        var ticks = utcNow.Ticks / window.Ticks * window.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private sealed record RateLimitPolicy(TimeSpan Window, int PermitLimit, int SegmentCount, int Amount = 1);
}
