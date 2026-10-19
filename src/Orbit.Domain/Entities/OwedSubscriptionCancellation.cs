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

    public void RecordExhaustionAlert(DateTime alertedAtUtc)
    {
        if (!ExhaustedAtUtc.HasValue)
            throw new InvalidOperationException("Cancellation retries are not exhausted.");
        if (alertedAtUtc.Kind != DateTimeKind.Utc || alertedAtUtc < NextAttemptAtUtc)
            throw new ArgumentOutOfRangeException(nameof(alertedAtUtc));
        NextAttemptAtUtc = alertedAtUtc.AddDays(1);
    }

    public void RecordFailure(DateTime attemptedAtUtc)
    {
        if (ExhaustedAtUtc.HasValue)
            throw new InvalidOperationException("Cancellation retries are exhausted.");
        if (attemptedAtUtc.Kind != DateTimeKind.Utc || attemptedAtUtc < NextAttemptAtUtc)
            throw new ArgumentOutOfRangeException(nameof(attemptedAtUtc));
        Attempts++;
        LastAttemptAtUtc = attemptedAtUtc;
        NextAttemptAtUtc = attemptedAtUtc.AddSeconds(Attempts switch { 1 => 60, 2 => 300, _ => 3600 });
        if (Attempts >= MaxAttempts)
        {
            ExhaustedAtUtc = attemptedAtUtc;
            NextAttemptAtUtc = attemptedAtUtc.AddDays(1);
        }
    }
}
