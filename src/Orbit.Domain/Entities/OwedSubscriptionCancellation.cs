using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public sealed class OwedSubscriptionCancellation : Entity
{
    public const int MaxAttempts = 27;

    public SubscriptionSource Provider { get; private set; }
    public string ProviderIdentifier { get; private set; } = string.Empty;
    public int Attempts { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastAttemptAtUtc { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public DateTime? ExhaustedAtUtc { get; private set; }

    private OwedSubscriptionCancellation() { }

    public static OwedSubscriptionCancellation Create(SubscriptionSource provider, string providerIdentifier)
    {
        if (provider is not (SubscriptionSource.Stripe or SubscriptionSource.GooglePlay))
            throw new ArgumentOutOfRangeException(nameof(provider));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerIdentifier);
        var createdAtUtc = DateTime.UtcNow;
        return new OwedSubscriptionCancellation
        {
            Provider = provider,
            ProviderIdentifier = providerIdentifier,
            CreatedAtUtc = createdAtUtc,
            NextAttemptAtUtc = createdAtUtc
        };
    }

    public void RecordAttempt(DateTime attemptedAtUtc)
    {
        if (attemptedAtUtc.Kind != DateTimeKind.Utc || attemptedAtUtc < NextAttemptAtUtc)
            throw new ArgumentOutOfRangeException(nameof(attemptedAtUtc));
        Attempts++;
        LastAttemptAtUtc = attemptedAtUtc;
        if (Attempts >= MaxAttempts)
            ExhaustedAtUtc ??= attemptedAtUtc;
        NextAttemptAtUtc = attemptedAtUtc.Add(RetryDelay);
    }

    public void RecordFailure(DateTime failedAtUtc)
    {
        if (!LastAttemptAtUtc.HasValue)
            throw new InvalidOperationException("Cancellation has not been attempted.");
        if (failedAtUtc.Kind != DateTimeKind.Utc || failedAtUtc < LastAttemptAtUtc)
            throw new ArgumentOutOfRangeException(nameof(failedAtUtc));
        NextAttemptAtUtc = failedAtUtc.Add(RetryDelay);
    }

    private TimeSpan RetryDelay => ExhaustedAtUtc.HasValue
        ? TimeSpan.FromDays(1)
        : TimeSpan.FromSeconds(Attempts switch { 1 => 60, 2 => 300, _ => 3600 });
}
