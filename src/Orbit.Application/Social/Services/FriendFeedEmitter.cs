using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Services;

/// <summary>
/// Classifies a gamification milestone into a <see cref="FriendFeedEvent"/> and appends it. Streak
/// milestones fire for every user as their streak crosses a tier; achievement-backed events arrive
/// only from the Pro achievement path. Emission is gated on the actor's social opt-in and de-duped
/// against already-stored events (an in-memory pre-check; the partial unique indexes are the backstop),
/// so an ordinary daily log that crosses no new tier writes nothing.
/// </summary>
public class FriendFeedEmitter(IGenericRepository<FriendFeedEvent> feedEventRepository) : IFriendFeedEventEmitter
{
    private static readonly Dictionary<string, int> VolumeCompletionCounts = new()
    {
        [AchievementDefinitions.GettingMomentum] = 10,
        [AchievementDefinitions.BuildingHabits] = 50,
        [AchievementDefinitions.Dedicated] = 100,
        [AchievementDefinitions.Relentless] = 500,
        [AchievementDefinitions.LegendaryVolume] = 1000,
    };

    public async Task EmitStreakMilestonesAsync(User actor, int previousStreak, CancellationToken cancellationToken = default)
    {
        if (!actor.SocialOptIn)
            return;

        var crossedTiers = AppConstants.StreakMilestoneTiers
            .Where(tier => previousStreak < tier && tier <= actor.CurrentStreak)
            .ToList();

        if (crossedTiers.Count == 0)
            return;

        var existing = await feedEventRepository.FindAsync(
            e => e.ActorUserId == actor.Id && e.Type == FriendFeedEventType.StreakMilestone,
            cancellationToken);
        var alreadyEmitted = existing.Select(e => e.Value).ToHashSet();

        foreach (var tier in crossedTiers)
        {
            if (!alreadyEmitted.Add(tier))
                continue;

            await feedEventRepository.AddAsync(FriendFeedEvent.StreakMilestone(actor.Id, tier), cancellationToken);
        }
    }

    public async Task EmitAchievementEventAsync(
        User actor,
        string achievementId,
        AchievementCategory category,
        CancellationToken cancellationToken = default)
    {
        if (!actor.SocialOptIn)
            return;

        var alreadyEmitted = await feedEventRepository.AnyAsync(
            e => e.ActorUserId == actor.Id && e.AchievementId == achievementId,
            cancellationToken);
        if (alreadyEmitted)
            return;

        var feedEvent = category == AchievementCategory.Volume
                && VolumeCompletionCounts.TryGetValue(achievementId, out var completions)
            ? FriendFeedEvent.HabitCompletedMilestone(actor.Id, achievementId, completions)
            : FriendFeedEvent.AchievementUnlocked(actor.Id, achievementId);

        await feedEventRepository.AddAsync(feedEvent, cancellationToken);
    }
}
