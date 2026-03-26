using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Gamification;

public class GetAchievementsQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepo = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly GetAchievementsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetAchievementsQueryHandlerTests()
    {
        _handler = new GetAchievementsQueryHandler(_userRepo, _achievementRepo);
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
        result.Value.Achievements.Should().HaveCount(AchievementDefinitions.All.Count);

        // Earned achievements should be marked
        var firstOrbit = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.FirstOrbit);
        firstOrbit.IsEarned.Should().BeTrue();
        firstOrbit.EarnedAtUtc.Should().NotBeNull();

        var liftoff = result.Value.Achievements.First(a => a.Id == AchievementDefinitions.Liftoff);
        liftoff.IsEarned.Should().BeTrue();
        liftoff.EarnedAtUtc.Should().NotBeNull();

        // Unearned achievements should be marked as not earned
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
        result.Value.Achievements.Should().HaveCount(AchievementDefinitions.All.Count);
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

        // Check that all achievements have required fields
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
    public async Task Handle_FreeUser_ReturnsPayGateFailure()
    {
        var user = CreateFreeUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var query = new GetAchievementsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
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
