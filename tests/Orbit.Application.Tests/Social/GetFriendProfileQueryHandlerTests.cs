using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class GetFriendProfileQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepository = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();

    private readonly GetFriendProfileQueryHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");
    private readonly User _friend = SocialTestHelpers.OptedInUser("Friend");

    public GetFriendProfileQueryHandlerTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var friendGraph = new FriendGraphService(_userRepository, _friendshipRepository, _blockedUserRepository);
        _handler = new GetFriendProfileQueryHandler(guard, friendGraph, _userRepository, _achievementRepository);

        SocialTestHelpers.StubUsers(_userRepository, _caller, _friend);
        SocialTestHelpers.StubFind(_achievementRepository);
    }

    private Friendship AcceptedFriendship()
    {
        var friendship = Friendship.Create(_caller.Id, _friend.Id).Value;
        friendship.Accept();
        return friendship;
    }

    [Fact]
    public async Task Handle_AcceptedFriend_ReturnsProfileStatsAndAchievements()
    {
        _friend.AddXp(1_200);
        _friend.SetStreakState(12, 40, new DateOnly(2026, 6, 1));
        SocialTestHelpers.StubFind(_friendshipRepository, AcceptedFriendship());
        SocialTestHelpers.StubFind(_achievementRepository,
            UserAchievement.Create(_friend.Id, AchievementDefinitions.FirstOrbit),
            UserAchievement.Create(_friend.Id, AchievementDefinitions.WeekWarrior));

        var result = await _handler.Handle(new GetFriendProfileQuery(_caller.Id, _friend.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var view = result.Value;
        view.UserId.Should().Be(_friend.Id);
        view.DisplayName.Should().Be("Friend");
        view.Handle.Should().NotBeNullOrWhiteSpace();
        view.CurrentStreak.Should().Be(12);
        view.Level.Should().Be(5);
        view.Achievements.Select(a => a.IconKey).Should().Contain(new[] { "first_orbit", "week_warrior" });
    }

    [Fact]
    public async Task Handle_NotAcceptedFriends_ReturnsUserNotFound()
    {
        SocialTestHelpers.StubFind(_friendshipRepository);

        var result = await _handler.Handle(new GetFriendProfileQuery(_caller.Id, _friend.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Handle_PendingFriendshipOnly_ReturnsUserNotFound()
    {
        SocialTestHelpers.StubFind(_friendshipRepository, Friendship.Create(_caller.Id, _friend.Id).Value);

        var result = await _handler.Handle(new GetFriendProfileQuery(_caller.Id, _friend.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Handle_CallerOptedOut_ReturnsSocialDisabled()
    {
        var caller = SocialTestHelpers.OptedOutUser("Private");
        SocialTestHelpers.StubUsers(_userRepository, caller, _friend);

        var result = await _handler.Handle(new GetFriendProfileQuery(caller.Id, _friend.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.SocialDisabled);
    }
}
