using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class DistributedRateLimitService(OrbitDbContext dbContext) : IDistributedRateLimitService
{
    private static readonly Dictionary<string, RateLimitPolicy> Policies =
        new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth"] = new(TimeSpan.FromMinutes(1), PermitLimit: 5, SegmentCount: 1),
            ["chat"] = new(TimeSpan.FromMinutes(1), PermitLimit: 20, SegmentCount: 4),
            ["support"] = new(TimeSpan.FromHours(1), PermitLimit: 3, SegmentCount: 1),
            // Bulk mutation endpoints (bulk create/delete/log/skip habits): scripts can otherwise
            // push thousands of rows in a burst. Scoped per user+ip partition.
            ["bulk"] = new(TimeSpan.FromMinutes(1), PermitLimit: 10, SegmentCount: 1),
            // Agent pending-operation execution: destructive-by-definition, require a tight
            // per-minute budget on top of step-up enforcement in AgentPolicyEvaluator.
            ["agent-execute"] = new(TimeSpan.FromMinutes(1), PermitLimit: 20, SegmentCount: 2)
        };

    public async Task<DistributedRateLimitDecision> TryAcquireAsync(
        string policyName,
        string partitionKey,
        CancellationToken cancellationToken = default)
    {
        if (!Policies.TryGetValue(policyName, out var policy))
            throw new InvalidOperationException($"Unknown rate-limit policy '{policyName}'.");

        var supportsRetryableTransactions =
            dbContext.Database.IsRelational() &&
            dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) != true;

        const int maxAttempts = 3;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            await using var transaction = supportsRetryableTransactions
                ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                : null;

            try
            {
                var decision = await TryAcquireCoreAsync(policyName, partitionKey, policy, cancellationToken);

                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);

                return decision;
            }
            catch (Exception ex) when (supportsRetryableTransactions && IsRetryableRateLimitConflict(ex) && attempt < maxAttempts - 1)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(cancellationToken);

                dbContext.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Rate-limit acquisition failed after retrying transactional conflicts.");
    }

    private async Task<DistributedRateLimitDecision> TryAcquireCoreAsync(
        string policyName,
        string partitionKey,
        RateLimitPolicy policy,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var segmentWindow = TimeSpan.FromTicks(policy.Window.Ticks / policy.SegmentCount);
        var segmentStartUtc = FloorUtc(now, segmentWindow);
        var segmentEndUtc = segmentStartUtc.Add(segmentWindow);
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

        var currentCount = recentBuckets.Sum(bucket => bucket.Count);
        if (currentCount >= policy.PermitLimit)
        {
            var oldestRelevantBucket = recentBuckets.FirstOrDefault();
            return new DistributedRateLimitDecision(
                false,
                policy.PermitLimit,
                currentCount,
                oldestRelevantBucket?.WindowEndsAtUtc ?? segmentEndUtc);
        }

        var currentBucket = recentBuckets.FirstOrDefault(bucket => bucket.WindowStartUtc == segmentStartUtc);
        if (currentBucket is null)
        {
            dbContext.DistributedRateLimitBuckets.Add(DistributedRateLimitBucket.Create(
                policyName,
                partitionKey,
                segmentStartUtc,
                segmentEndUtc));
        }
        else
        {
            currentBucket.Increment();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new DistributedRateLimitDecision(
            true,
            policy.PermitLimit,
            currentCount + 1,
            segmentEndUtc);
    }

    private static bool IsRetryableRateLimitConflict(Exception exception)
    {
        return exception switch
        {
            DbUpdateException dbUpdateException => IsRetryableRateLimitConflict(dbUpdateException.InnerException ?? dbUpdateException),
            PostgresException postgresException => postgresException.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure,
            _ => false
        };
    }

    private static DateTime FloorUtc(DateTime utcNow, TimeSpan window)
    {
        var ticks = utcNow.Ticks / window.Ticks * window.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private sealed record RateLimitPolicy(TimeSpan Window, int PermitLimit, int SegmentCount);
}
