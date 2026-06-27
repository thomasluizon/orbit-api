using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

/// <summary>
/// A single milestone moment in the warm friend feed, written one row per actor at the moment
/// the milestone occurs. The feed query fans these out on read to a caller's accepted, opted-in,
/// non-blocked friends, so there is no per-friend write amplification and new friends retroactively
/// see past events. Inputs come from trusted internal gamification code, so the factories are plain
/// (no <see cref="Result{T}"/> validation), mirroring <see cref="UserAchievement"/>.
/// </summary>
public class FriendFeedEvent : Entity
{
    public Guid ActorUserId { get; private set; }
    public FriendFeedEventType Type { get; private set; }
    public int? Value { get; private set; }
    public string? AchievementId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    private FriendFeedEvent() { }

    public static FriendFeedEvent StreakMilestone(Guid actorUserId, int streakDays) =>
        new()
        {
            ActorUserId = actorUserId,
            Type = FriendFeedEventType.StreakMilestone,
            Value = streakDays,
            CreatedAtUtc = DateTime.UtcNow
        };

    public static FriendFeedEvent AchievementUnlocked(Guid actorUserId, string achievementId) =>
        new()
        {
            ActorUserId = actorUserId,
            Type = FriendFeedEventType.AchievementUnlocked,
            AchievementId = achievementId,
            CreatedAtUtc = DateTime.UtcNow
        };

    public static FriendFeedEvent HabitCompletedMilestone(Guid actorUserId, string achievementId, int completions) =>
        new()
        {
            ActorUserId = actorUserId,
            Type = FriendFeedEventType.HabitCompletedMilestone,
            Value = completions,
            AchievementId = achievementId,
            CreatedAtUtc = DateTime.UtcNow
        };
}
