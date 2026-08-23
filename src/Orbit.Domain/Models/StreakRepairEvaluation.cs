namespace Orbit.Domain.Models;

public sealed record StreakRepairEvaluation(
    DateOnly MissedDate,
    bool IsAvailable,
    UserStreakState? RepairedState)
{
    public static StreakRepairEvaluation Unavailable(DateOnly missedDate) =>
        new(missedDate, false, null);

    public static StreakRepairEvaluation Available(DateOnly missedDate, UserStreakState repairedState) =>
        new(missedDate, true, repairedState);
}
