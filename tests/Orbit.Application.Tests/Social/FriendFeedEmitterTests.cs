using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class FriendFeedEmitterTests
{
    private readonly IGenericRepository<FriendFeedEvent> _feedRepository = Substitute.For<IGenericRepository<FriendFeedEvent>>();
    private readonly FriendFeedEmitter _emitter;

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public FriendFeedEmitterTests()
    {
        _emitter = new FriendFeedEmitter(_feedRepository);
        SocialTestHelpers.StubFind(_feedRepository);
    }

    private static User ActorWithStreak(int currentStreak, bool optedIn = true)
    {
        var user = optedIn ? SocialTestHelpers.OptedInUser("Actor") : SocialTestHelpers.OptedOutUser("Actor");
        user.SetStreakState(currentStreak, currentStreak, Today);
        return user;
    }

    [Fact]
    public async Task StreakMilestone_CrossingTier_EmitsOneEvent()
    {
        var actor = ActorWithStreak(7);

        await _emitter.EmitStreakMilestonesAsync(actor, previousStreak: 6, CancellationToken.None);

        await _feedRepository.Received(1).AddAsync(
            Arg.Is<FriendFeedEvent>(e => e.Type == FriendFeedEventType.StreakMilestone && e.Value == 7 && e.ActorUserId == actor.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreakMilestone_CrossingMultipleTiersAtOnce_EmitsEach()
    {
        var actor = ActorWithStreak(30);

        await _emitter.EmitStreakMilestonesAsync(actor, previousStreak: 6, CancellationToken.None);

        await _feedRepository.Received(1).AddAsync(Arg.Is<FriendFeedEvent>(e => e.Value == 7), Arg.Any<CancellationToken>());
        await _feedRepository.Received(1).AddAsync(Arg.Is<FriendFeedEvent>(e => e.Value == 14), Arg.Any<CancellationToken>());
        await _feedRepository.Received(1).AddAsync(Arg.Is<FriendFeedEvent>(e => e.Value == 30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreakMilestone_OrdinaryDailyLog_CrossesNoTier_EmitsNothing()
    {
        var actor = ActorWithStreak(10);

        await _emitter.EmitStreakMilestonesAsync(actor, previousStreak: 9, CancellationToken.None);

        await _feedRepository.DidNotReceive().AddAsync(Arg.Any<FriendFeedEvent>(), Arg.Any<CancellationToken>());
        await _feedRepository.DidNotReceive().FindAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<FriendFeedEvent, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreakMilestone_AlreadyEmittedTier_IsNotDuplicated()
    {
        var actor = ActorWithStreak(7);
        SocialTestHelpers.StubFind(_feedRepository, FriendFeedEvent.StreakMilestone(actor.Id, 7));

        await _emitter.EmitStreakMilestonesAsync(actor, previousStreak: 6, CancellationToken.None);

        await _feedRepository.DidNotReceive().AddAsync(Arg.Any<FriendFeedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreakMilestone_OptedOutActor_EmitsNothing()
    {
        var actor = ActorWithStreak(7, optedIn: false);

        await _emitter.EmitStreakMilestonesAsync(actor, previousStreak: 6, CancellationToken.None);

        await _feedRepository.DidNotReceive().AddAsync(Arg.Any<FriendFeedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Achievement_VolumeCategory_EmitsHabitCompletedMilestoneWithCount()
    {
        var actor = SocialTestHelpers.OptedInUser("Actor");

        await _emitter.EmitAchievementEventAsync(actor, AchievementDefinitions.Dedicated, AchievementCategory.Volume, CancellationToken.None);

        await _feedRepository.Received(1).AddAsync(
            Arg.Is<FriendFeedEvent>(e => e.Type == FriendFeedEventType.HabitCompletedMilestone
                                         && e.AchievementId == AchievementDefinitions.Dedicated
                                         && e.Value == 100),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Achievement_NonVolumeCategory_EmitsAchievementUnlocked()
    {
        var actor = SocialTestHelpers.OptedInUser("Actor");

        await _emitter.EmitAchievementEventAsync(actor, AchievementDefinitions.PerfectDay, AchievementCategory.Perfection, CancellationToken.None);

        await _feedRepository.Received(1).AddAsync(
            Arg.Is<FriendFeedEvent>(e => e.Type == FriendFeedEventType.AchievementUnlocked
                                         && e.AchievementId == AchievementDefinitions.PerfectDay
                                         && e.Value == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Achievement_AlreadyEmitted_IsNotDuplicated()
    {
        var actor = SocialTestHelpers.OptedInUser("Actor");
        _feedRepository.AnyAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<FriendFeedEvent, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _emitter.EmitAchievementEventAsync(actor, AchievementDefinitions.PerfectDay, AchievementCategory.Perfection, CancellationToken.None);

        await _feedRepository.DidNotReceive().AddAsync(Arg.Any<FriendFeedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Achievement_OptedOutActor_EmitsNothing()
    {
        var actor = SocialTestHelpers.OptedOutUser("Actor");

        await _emitter.EmitAchievementEventAsync(actor, AchievementDefinitions.PerfectDay, AchievementCategory.Perfection, CancellationToken.None);

        await _feedRepository.DidNotReceive().AddAsync(Arg.Any<FriendFeedEvent>(), Arg.Any<CancellationToken>());
    }
}
