using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

/// <summary>
/// Pure validation guards for <see cref="Challenge"/> invariants. Each method returns the matching
/// <see cref="DomainErrors"/> entry on violation (or null when valid), exactly as the factory expects.
/// Enforces type/target coherence (CoopGoal needs a positive target, StreakTogether forbids one) and
/// period ordering.
/// </summary>
internal static class ChallengeInvariants
{
    public static AppError? ValidateTypeAndTarget(ChallengeType type, int? targetCount)
    {
        if (type == ChallengeType.CoopGoal && (targetCount is null || targetCount <= 0))
            return DomainErrors.ChallengeTargetRequired;

        if (type == ChallengeType.StreakTogether && targetCount is not null)
            return DomainErrors.ChallengeTargetNotAllowed;

        return null;
    }

    public static AppError? ValidatePeriod(DateOnly periodStartUtc, DateOnly? periodEndUtc)
    {
        if (periodEndUtc.HasValue && periodEndUtc.Value < periodStartUtc)
            return DomainErrors.ChallengePeriodInvalid;

        return null;
    }
}
