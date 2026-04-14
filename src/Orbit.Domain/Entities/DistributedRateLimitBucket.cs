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

    private DistributedRateLimitBucket()
    {
    }

    public static DistributedRateLimitBucket Create(
        string policyName,
        string partitionKey,
        DateTime windowStartUtc,
        DateTime windowEndsAtUtc)
    {
        return new DistributedRateLimitBucket
        {
            PolicyName = policyName,
            PartitionKey = partitionKey,
            WindowStartUtc = windowStartUtc,
            WindowEndsAtUtc = windowEndsAtUtc,
            Count = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    public void Increment()
    {
        Count++;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
