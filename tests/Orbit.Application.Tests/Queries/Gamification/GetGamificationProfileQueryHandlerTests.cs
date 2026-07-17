using FluentAssertions;
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
    private readonly GetGamificationProfileQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetGamificationProfileQueryHandlerTests()
    {
        _featureFlagService.GetEnabledKeysForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());
        _progressService.LoadAsync(Arg.Any<User>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(AchievementProgressMetrics.Empty);
        _handler = new GetGamificationProfileQueryHandler(_userRepo, _achievementRepo, _featureFlagService, _progressService);
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
        result.Value.AchievementsEarned.Should().Be(2);
        result.Value.AchievementsTotal.Should().Be(AchievementDefinitions.All.Count);
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
    }

    [Fact]
    public async Task Handle_FreeUser_FlagOn_ExposesXpLevelStreak_HidesAchievements()
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
        result.Value.AchievementsLocked.Should().BeTrue();
        result.Value.Achievements.Should().BeEmpty();
        result.Value.AchievementsEarned.Should().Be(0);
        result.Value.AchievementsTotal.Should().Be(AchievementDefinitions.All.Count);
        result.Value.NextReward.ProTeaser.Should().NotBeNull();
        result.Value.NextReward.ProTeaser!.Kind.Should().Be("achievements");
        result.Value.NextReward.ProTeaser.Locked.Should().BeTrue();
        result.Value.NextReward.NextLevel.Should().Be(3);
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
                FriendsCount: 3, CheersSent: 10, EarlyLogs: 4, NightLogs: 0));

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
