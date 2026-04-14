namespace Orbit.Domain.Models;

public record DistributedRateLimitDecision(
    bool Allowed,
    int PermitLimit,
    int CurrentCount,
    DateTime WindowEndsAtUtc);
