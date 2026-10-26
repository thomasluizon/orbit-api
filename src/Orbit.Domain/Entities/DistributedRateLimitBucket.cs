using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

public class DistributedRateLimitBucket : Entity
{
    public string PolicyName { get; private set; } = null!;
    public string PartitionKey { get; private set; } = null!;
    public DateTime WindowStartUtc { get; private set; }
    public DateTime WindowEndsAtUtc { get; private set; }
    public int Count { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private DistributedRateLimitBucket() { }

    public static DistributedRateLimitBucket Create(
        string policyName,
        string partitionKey,
        DateTime windowStartUtc,
        DateTime windowEndsAtUtc,
        int amount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, 1);
        if (policyName.Length > 64 || partitionKey.Length > 256 || windowEndsAtUtc <= windowStartUtc)
            throw new ArgumentException("Invalid rate-limit bucket.");

        return new DistributedRateLimitBucket
        {
            PolicyName = policyName,
            PartitionKey = partitionKey,
            WindowStartUtc = windowStartUtc,
            WindowEndsAtUtc = windowEndsAtUtc,
            Count = amount,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public void Increment(int amount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, 1);
        Count = checked(Count + amount);
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
