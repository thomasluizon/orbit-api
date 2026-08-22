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

public class GetGamificationProfileQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepo = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IFeatureFlagService _featureFlagService = Substitute.For<IFeatureFlagService>();
    private readonly IAchievementProgressService _progressService = Substitute.For<IAchievementProgressService>();
    private readonly IProductAnalytics _productAnalytics = Substitute.For<IProductAnalytics>();
    private readonly GetGamificationProfileQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetGamificationProfileQueryHandlerTests()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _progressService.LoadAsync(Arg.Any<User>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(AchievementProgressMetrics.Empty);
        _handler = new GetGamificationProfileQueryHandler(
            _userRepo,
            _achievementRepo,
            _featureFlagService,
            _progressService,
            _productAnalytics,
            Substitute.For<ILogger<GetGamificationProfileQueryHandler>>());
    }

    private void EnableFreeTierFlag()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { FeatureFlagKeys.GamificationFreeTier });
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
    public async Task Handle_ProUser_ReturnsProfile()
    {
        var user = CreateProUser();
        user.AddXp(150);
        user.SetLevel(2);
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

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalXp.Should().Be(150);
        result.Value.Level.Should().Be(2);
        result.Value.LevelTitle.Should().Be("Explorer");
        result.Value.LevelTitleKey.Should().Be("explorer");
        result.Value.AchievementsEarned.Should().Be(2);
        result.Value.AchievementsTotal.Should().Be(32);
        result.Value.Achievements.Should().HaveCount(32);
        _productAnalytics.Received(1).CaptureUserEvent(
            user.Id,
            "achievements_viewed",
            "Pro",
            Arg.Is<IReadOnlyDictionary<string, object>>(properties =>
                properties["isPro"].Equals(true) && properties["earnedCount"].Equals(2)));
    }

    [Fact]
    public async Task Handle_ProUser_CalculatesXpToNextLevel()
    {
        var user = CreateProUser();
        user.AddXp(200); _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>());

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.XpToNextLevel.Should().Be(100);
    }

    [Fact]
    public async Task Handle_AtLevel10_ReturnsInfiniteNextLevel()
    {
        var user = CreateProUser();
        user.AddXp(10_000);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>());

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Level.Should().Be(10);
        result.Value.XpToNextLevel.Should().Be(2_100);
        result.Value.XpForNextLevel.Should().Be(12_100);
    }

    [Fact]
    public async Task Handle_ProUserPast10_ComputesInfiniteNextLevel()
    {
        var user = CreateProUser();
        user.AddXp(15_000);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>());

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Level.Should().Be(12);
        result.Value.LevelTitle.Should().Be("Legend");
        result.Value.XpToNextLevel.Should().Be(1_900);
        result.Value.IsPro.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_FreeUser_FlagOff_ReturnsPayGateFailure()
    {
        var user = CreateFreeUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        _productAnalytics.DidNotReceiveWithAnyArgs().CaptureUserEvent(default, default!, default!, default);
    }

    [Fact]
    public async Task Handle_FreeUser_FlagOn_ReturnsActiveAchievementsWithoutLockOrTeaser()
    {
        var user = CreateFreeUser();
        user.AddXp(150);
        user.SetStreakState(5, 12, new DateOnly(2026, 6, 20));
        EnableFreeTierFlag();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement> { UserAchievement.Create(UserId, AchievementDefinitions.FirstOrbit) });

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalXp.Should().Be(150);
        result.Value.Level.Should().Be(2);
        result.Value.CurrentStreak.Should().Be(5);
        result.Value.LongestStreak.Should().Be(12);
        result.Value.IsPro.Should().BeFalse();
        result.Value.AchievementsLocked.Should().BeFalse();
        result.Value.Achievements.Should().HaveCount(32);
        result.Value.AchievementsEarned.Should().Be(1);
        result.Value.Achievements.Count(a => a.IsEarned).Should().Be(result.Value.AchievementsEarned);
        result.Value.AchievementsTotal.Should().Be(32);
        result.Value.NextReward.ProTeaser.Should().BeNull();
        result.Value.NextReward.NextLevel.Should().Be(3);
        _productAnalytics.Received(1).CaptureUserEvent(
            user.Id,
            "achievements_viewed",
            "Free",
            Arg.Is<IReadOnlyDictionary<string, object>>(properties =>
                properties["isPro"].Equals(false) && properties["earnedCount"].Equals(1)));
    }

    [Fact]
    public async Task Handle_ProUser_FlagOff_ReturnsFullProfile_WithAchievements_NoTeaser()
    {
        var user = CreateProUser();
        user.AddXp(150);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement> { UserAchievement.Create(UserId, AchievementDefinitions.FirstOrbit) });

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsPro.Should().BeTrue();
        result.Value.AchievementsLocked.Should().BeFalse();
        result.Value.Achievements.Should().NotBeEmpty();
        result.Value.AchievementsEarned.Should().Be(1);
        result.Value.NextReward.ProTeaser.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ProUser_PopulatesPerAchievementProgress()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement> { UserAchievement.Create(UserId, AchievementDefinitions.WeekWarrior) });
        _progressService.LoadAsync(Arg.Any<User>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(new AchievementProgressMetrics(
                CurrentStreak: 5, TotalCompletions: 120, GoalsCreated: 1, GoalsCompleted: 2,
                EarlyLogs: 4, NightLogs: 0));

        var result = await _handler.Handle(new GetGamificationProfileQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var monthlyMaster = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.MonthlyMaster);
        monthlyMaster.ProgressCurrent.Should().Be(5);
        monthlyMaster.ProgressTarget.Should().Be(30);

        var dedicated = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.Dedicated);
        dedicated.ProgressCurrent.Should().Be(100);
        dedicated.ProgressTarget.Should().Be(100);

        var firstOrbit = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.FirstOrbit);
        firstOrbit.ProgressCurrent.Should().BeNull();
        firstOrbit.ProgressTarget.Should().BeNull();

        var weekWarrior = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.WeekWarrior);
        weekWarrior.IsEarned.Should().BeTrue();
        weekWarrior.ProgressCurrent.Should().Be(7);
        weekWarrior.ProgressTarget.Should().Be(7);
    }

    [Fact]
    public async Task Handle_RetiredAchievementEarned_IncludesHistoricalBadgeOutsideActiveTotal()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _achievementRepo.FindAsync(
                Arg.Any<Expression<Func<UserAchievement, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>
            {
                UserAchievement.Create(UserId, AchievementDefinitions.BattleBuddy)
            });

        var result = await _handler.Handle(new GetGamificationProfileQuery(UserId), CancellationToken.None);

        result.Value.AchievementsTotal.Should().Be(32);
        result.Value.AchievementsEarned.Should().Be(1);
        result.Value.Achievements.Should().HaveCount(33);
        var retired = result.Value.Achievements.Single(a => a.Id == AchievementDefinitions.BattleBuddy);
        retired.IsEarned.Should().BeTrue();
        retired.EarnedAtUtc.Should().NotBeNull();
        retired.Name.Should().Be("Battle Buddy");
        retired.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }

    [Fact]
    public async Task Handle_NewUser_ReturnsLevel1()
    {
        var user = CreateProUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>());

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalXp.Should().Be(0);
        result.Value.Level.Should().Be(1);
        result.Value.LevelTitle.Should().Be("Starter");
        result.Value.AchievementsEarned.Should().Be(0);
    }
}
