using Orbit.Domain.Common;

namespace Orbit.Domain.Entities;

/// <summary>
/// Shared base for idempotency-ledger entities that record an external provider event
/// (Stripe webhook, Google Play RTDN) once it has been handled. Holds the processing
/// timestamp; each derived type owns its provider-specific unique key.
/// </summary>
public abstract class ProcessedExternalEvent : Entity
{
    public DateTime ProcessedAtUtc { get; protected set; }
}
