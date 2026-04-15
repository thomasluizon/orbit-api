using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IUserStreakService
{
    /// <summary>
    /// Recompute the user's streak state from logs, freezes, and habit schedules.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="awardFreezeIfEligible">
    /// When true (default), milestones reached during recalc grant a streak freeze.
    /// Pass false from unlog or other passive recompute paths to avoid awarding
    /// freezes from streaks the user is no longer credited for.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<UserStreakState?> RecalculateAsync(
        Guid userId,
        CancellationToken cancellationToken = default,
        bool awardFreezeIfEligible = true);
}
