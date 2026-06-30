namespace Orbit.Application.Challenges.Services;

/// <summary>
/// Post-log seam for cooperative challenges. Invoked after a habit is logged to recompute the shared
/// progress of the user's active CoopGoal challenges that include the habit, completing any whose target
/// the new log just reached and awarding Mission Accomplished to their participants. Streak challenges
/// have no completion event, so they are evaluated read-side only.
/// </summary>
public interface IChallengeProgressService
{
    Task EvaluateOnHabitLoggedAsync(Guid userId, Guid habitId, CancellationToken cancellationToken = default);
}
