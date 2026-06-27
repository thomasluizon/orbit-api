using FluentAssertions;
using NSubstitute;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class GetFriendFeedQueryTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepository = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IFriendFeedReader _feedReader = Substitute.For<IFriendFeedReader>();
    private readonly GetFriendFeedQueryHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");

    public GetFriendFeedQueryTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var friendGraph = new FriendGraphService(_userRepository, _friendshipRepository, _blockedUserRepository);
        _handler = new GetFriendFeedQueryHandler(guard, friendGraph, _blockedUserRepository, _userRepository, _feedReader);
    }

    private static Friendship Accepted(Guid a, Guid b)
    {
        var friendship = Friendship.Create(a, b).Value;
        friendship.Accept();
        return friendship;
    }

    private void StubReader(params FriendFeedEvent[] events) =>
        _feedReader.ReadFeedPageAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<FriendFeedEvent>)events.ToList());

    [Fact]
    public async Task Feed_IncludesOnlyAcceptedNonBlockedOptedInFriends()
    {
        var goodFriend = SocialTestHelpers.OptedInUser("Good");
        var optedOutFriend = SocialTestHelpers.OptedOutUser("Quiet");
        var blockedFriend = SocialTestHelpers.OptedInUser("Blocked");

        SocialTestHelpers.StubUsers(_userRepository, _caller, goodFriend, optedOutFriend, blockedFriend);
        SocialTestHelpers.StubFind(_friendshipRepository,
            Accepted(_caller.Id, goodFriend.Id),
            Accepted(_caller.Id, optedOutFriend.Id),
            Accepted(_caller.Id, blockedFriend.Id));
        SocialTestHelpers.StubFind(_blockedUserRepository, BlockedUser.Create(_caller.Id, blockedFriend.Id).Value);
        StubReader(FriendFeedEvent.StreakMilestone(goodFriend.Id, 7));

        var result = await _handler.Handle(new GetFriendFeedQuery(_caller.Id, null, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _feedReader.Received(1).ReadFeedPageAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(goodFriend.Id)),
            Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        result.Value.Items.Should().ContainSingle(i => i.ActorUserId == goodFriend.Id && i.ActorDisplayName == "Good");
    }

    [Fact]
    public async Task Feed_NoFriends_ReturnsEmptyPageWithoutQueryingReader()
    {
        SocialTestHelpers.StubUsers(_userRepository, _caller);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await _handler.Handle(new GetFriendFeedQuery(_caller.Id, null, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.NextCursor.Should().BeNull();
        await _feedReader.DidNotReceive().ReadFeedPageAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Feed_FullPage_EmitsNextCursorThatPagesStably()
    {
        var friend = SocialTestHelpers.OptedInUser("Friend");
        SocialTestHelpers.StubUsers(_userRepository, _caller, friend);
        SocialTestHelpers.StubFind(_friendshipRepository, Accepted(_caller.Id, friend.Id));
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var first = FriendFeedEvent.StreakMilestone(friend.Id, 30);
        var second = FriendFeedEvent.StreakMilestone(friend.Id, 14);
        var third = FriendFeedEvent.StreakMilestone(friend.Id, 7);
        StubReader(first, second, third);

        var page1 = await _handler.Handle(new GetFriendFeedQuery(_caller.Id, null, 2), CancellationToken.None);

        page1.Value.Items.Should().HaveCount(2);
        page1.Value.NextCursor.Should().NotBeNull();

        _feedReader.ClearReceivedCalls();
        StubReader();

        await _handler.Handle(new GetFriendFeedQuery(_caller.Id, page1.Value.NextCursor, 2), CancellationToken.None);

        await _feedReader.Received(1).ReadFeedPageAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), second.CreatedAtUtc, second.Id, 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Feed_LastPage_HasNullNextCursor()
    {
        var friend = SocialTestHelpers.OptedInUser("Friend");
        SocialTestHelpers.StubUsers(_userRepository, _caller, friend);
        SocialTestHelpers.StubFind(_friendshipRepository, Accepted(_caller.Id, friend.Id));
        SocialTestHelpers.StubFind(_blockedUserRepository);
        StubReader(FriendFeedEvent.StreakMilestone(friend.Id, 7));

        var result = await _handler.Handle(new GetFriendFeedQuery(_caller.Id, null, 2), CancellationToken.None);

        result.Value.Items.Should().HaveCount(1);
        result.Value.NextCursor.Should().BeNull();
    }
}
