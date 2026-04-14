using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

/// <summary>
/// Evaluates whether a user should earn streak freezes after their streak is updated.
/// </summary>
public interface IStreakFreezeEarnService
{
    /// <summary>
    /// Evaluates the user's current streak against the last-earn anchor and mutates
    /// the tracked user entity. The caller is responsible for committing changes via
    /// <see cref="IUnitOfWork.SaveChangesAsync"/>.
    /// </summary>
    Task<StreakFreezeEarnOutcome> EvaluateAsync(Guid userId, CancellationToken cancellationToken = default);
}
