using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Gamification.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetAchievementsQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepo = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();
    private readonly IAchievementProgressService _progressService = Substitute.For<IAchievementProgressService>();
    private readonly IProductAnalytics _productAnalytics = Substitute.For<IProductAnalytics>();
    private readonly GetAchievementsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetAchievementsQueryHandlerTests()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.GamificationFreeTier });
        _progressService.LoadAsync(Arg.Any<User>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(AchievementProgressMetrics.Empty);
        _handler = new GetAchievementsQueryHandler(
            _userRepo,
            _achievementRepo,
            _featureFlagService,
            _progressService,
            _productAnalytics,
            Substitute.For<ILogger<GetAchievementsQueryHandler>>());
    }

    private static User CreateProUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddYears(1));
        return user;
    }

    private static User CreateFreeUser()
    {
        var user = User.Create("Test User", "test@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        return user;
    }

    [Fact]
    public async Task Handle_ProUser_ReturnsAllAchievementsWithEarnedStatus()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var earned = new List<UserAchievement>
        {
            UserAchievement.Create(UserId, AchievementDefinitions.FirstOrbit),
            UserAchievement.Create(UserId, AchievementDefinitions.Liftoff)
        };
        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(earned);

        var query = new GetAchievementsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Achievements.Should().HaveCount(32);

        var firstOrbit = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.FirstOrbit);
        firstOrbit.IsEarned.Should().BeTrue();
        firstOrbit.EarnedAtUtc.Should().NotBeNull();

        var liftoff = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.Liftoff);
        liftoff.IsEarned.Should().BeTrue();
        liftoff.EarnedAtUtc.Should().NotBeNull();

        var weekWarrior = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.WeekWarrior);
        weekWarrior.IsEarned.Should().BeFalse();
        weekWarrior.EarnedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ProUser_NoEarnedAchievements_AllUnearned()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>());

        var query = new GetAchievementsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Achievements.Should().HaveCount(32);
        result.Value.Achievements.Should().AllSatisfy(a =>
        {
            a.IsEarned.Should().BeFalse();
            a.EarnedAtUtc.Should().BeNull();
        });
    }

    [Fact]
    public async Task Handle_ProUser_AchievementsHaveCorrectProperties()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>());

        var query = new GetAchievementsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        result.Value.Achievements.Should().AllSatisfy(a =>
        {
            a.Id.Should().NotBeNullOrEmpty();
            a.Name.Should().NotBeNullOrEmpty();
            a.Description.Should().NotBeNullOrEmpty();
            a.Category.Should().NotBeNullOrEmpty();
            a.Rarity.Should().NotBeNullOrEmpty();
            a.XpReward.Should().BeGreaterThan(0);
            a.IconKey.Should().NotBeNullOrEmpty();
        });
    }

    [Fact]
    public async Task Handle_ProUser_AttachesProgressForQuantifiableAndNullForOneShot()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement> { UserAchievement.Create(UserId, AchievementDefinitions.GettingMomentum) });
        _progressService.LoadAsync(Arg.Any<User>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(new AchievementProgressMetrics(
                CurrentStreak: 12, TotalCompletions: 40, GoalsCreated: 2, GoalsCompleted: 0,
                EarlyLogs: 3, NightLogs: 9));

        var result = await _handler.Handle(new GetAchievementsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        var fortnight = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.FortnightFocus);
        fortnight.ProgressCurrent.Should().Be(12);
        fortnight.ProgressTarget.Should().Be(14);

        var nightOwl = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.NightOwl);
        nightOwl.ProgressCurrent.Should().Be(9);
        nightOwl.ProgressTarget.Should().Be(10);

        var gettingMomentum = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.GettingMomentum);
        gettingMomentum.IsEarned.Should().BeTrue();
        gettingMomentum.ProgressCurrent.Should().Be(10);
        gettingMomentum.ProgressTarget.Should().Be(10);

        var missionControl = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.MissionControl);
        missionControl.ProgressCurrent.Should().BeNull();
        missionControl.ProgressTarget.Should().BeNull();
    }

    [Fact]
    public async Task Handle_FreeUser_ReturnsActiveAchievementsAndViewedEvent()
    {
        var user = CreateFreeUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var query = new GetAchievementsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Achievements.Should().HaveCount(32);
        _productAnalytics.Received(1).CaptureUserEvent(
            user.Id,
            "achievements_viewed",
            "Free",
            Arg.Is<IReadOnlyDictionary<string, object>>(properties =>
                properties["isPro"].Equals(false) && properties["earnedCount"].Equals(0)));
    }

    [Fact]
    public async Task Handle_FreeUser_FlagOff_ReturnsPayGateFailure()
    {
        var user = CreateFreeUser();
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _handler.Handle(new GetAchievementsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task Handle_RetiredAchievementEarned_IncludesHistoricalBadge()
    {
        var user = CreateFreeUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _achievementRepo.FindAsync(
                Arg.Any<Expression<Func<UserAchievement, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>
            {
                UserAchievement.Create(UserId, AchievementDefinitions.BattleBuddy)
            });

        var result = await _handler.Handle(new GetAchievementsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Achievements.Should().HaveCount(33);
        result.Value.Achievements.Single(a => a.Id == AchievementDefinitions.BattleBuddy)
            .IsEarned.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetAchievementsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }
}
