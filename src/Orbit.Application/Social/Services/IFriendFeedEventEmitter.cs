using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Application.Social.Services;

/// <summary>
/// The friend-feed write pipeline injected into the two gamification hooks. Emission is opt-in-gated on
/// the actor and idempotent (de-duped against prior events). Rows are added to the active unit of work;
/// the caller persists them with its own SaveChanges so emission shares the milestone's transaction.
/// </summary>
public interface IFriendFeedEventEmitter
{
    Task EmitStreakMilestonesAsync(User actor, int previousStreak, CancellationToken cancellationToken = default);

    Task EmitAchievementEventAsync(
        User actor,
        string achievementId,
        AchievementCategory category,
        CancellationToken cancellationToken = default);
}
