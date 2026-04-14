namespace Orbit.Domain.Models;

/// <summary>
/// Describes the result of evaluating whether a user should earn new streak freezes
/// after their streak has been recalculated.
/// </summary>
/// <param name="FreezesEarned">Freezes actually granted to the user's balance in this call (clamped by the hold cap).</param>
/// <param name="FreezesCapped">Freezes that would have been granted but hit the accumulation cap (telemetry).</param>
/// <param name="NewBalance">The user's balance after applying earned freezes.</param>
/// <param name="NewLastEarnedAtStreak">The anchor value stored on the user after the evaluation.</param>
public record StreakFreezeEarnOutcome(
    int FreezesEarned,
    int FreezesCapped,
    int NewBalance,
    int NewLastEarnedAtStreak);
