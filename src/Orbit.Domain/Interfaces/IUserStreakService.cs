using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IUserStreakService
{
    Task<IReadOnlyList<DateOnly>> GetRepairableGapDatesAsync(
        Guid userId,
        DateOnly userToday,
        CancellationToken cancellationToken = default);

    Task<UserStreakState?> EvaluateGapRepairAsync(
        Guid userId,
        DateOnly userToday,
        IReadOnlyCollection<DateOnly> dates,
        CancellationToken cancellationToken = default);

    Task<UserStreakState?> RecalculateAsync(
        Guid userId,
        bool awardFreezeIfEligible = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the user's current streak from persisted data without changing tracked state.
    /// </summary>
    Task<UserStreakState?> CalculateAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates whether local yesterday can be repaired with one banked freeze without changing state.
    /// </summary>
    Task<StreakRepairEvaluation?> EvaluateRepairAsync(
        Guid userId,
        DateOnly userToday,
        DateOnly missedDate,
        CancellationToken cancellationToken = default);
}
