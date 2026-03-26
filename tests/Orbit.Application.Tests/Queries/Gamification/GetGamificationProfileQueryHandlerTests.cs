using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetGamificationProfileQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepo = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly GetGamificationProfileQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetGamificationProfileQueryHandlerTests()
    {
        _handler = new GetGamificationProfileQueryHandler(_userRepo, _achievementRepo);
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
        user.AddXp(200); // Level 2 (100-299), next level at 300
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>());

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.XpToNextLevel.Should().Be(100); // 300 - 200
    }

    [Fact]
    public async Task Handle_MaxLevel_XpToNextLevelIsNull()
    {
        var user = CreateProUser();
        user.AddXp(10_000); // Level 10, max
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _achievementRepo.FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<UserAchievement>());

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.XpToNextLevel.Should().BeNull();
    }

    [Fact]
    public async Task Handle_FreeUser_ReturnsPayGateFailure()
    {
        var user = CreateFreeUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var query = new GetGamificationProfileQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
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
        // Fresh user with 0 XP
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
