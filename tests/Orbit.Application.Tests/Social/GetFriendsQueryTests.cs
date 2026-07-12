using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class GetFriendsQueryTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly ISocialGraphReader _reader = Substitute.For<ISocialGraphReader>();
    private readonly GetFriendsQueryHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");

    public GetFriendsQueryTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        _handler = new GetFriendsQueryHandler(guard, _reader, _userRepository);
    }

    private void StubReader(params Friendship[] friendships) =>
        _reader.ReadVisibleFriendshipsAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<Friendship>)friendships.ToList());

    private static Friendship Accepted(Guid requester, Guid addressee)
    {
        var friendship = Friendship.Create(requester, addressee).Value;
        friendship.Accept();
        return friendship;
    }

    [Fact]
    public async Task PartitionsAcceptedIncomingAndOutgoing()
    {
        var friend = SocialTestHelpers.OptedInUser("Friend");
        var incomingRequester = SocialTestHelpers.OptedInUser("Incoming");
        var outgoingAddressee = SocialTestHelpers.OptedInUser("Outgoing");
        SocialTestHelpers.StubUsers(_userRepository, _caller, friend, incomingRequester, outgoingAddressee);
        StubReader(
            Accepted(_caller.Id, friend.Id),
            Friendship.Create(incomingRequester.Id, _caller.Id).Value,
            Friendship.Create(_caller.Id, outgoingAddressee.Id).Value);

        var result = await _handler.Handle(new GetFriendsQuery(_caller.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Friends.Should().ContainSingle(f => f.UserId == friend.Id);
        result.Value.IncomingRequests.Should().ContainSingle(r => r.UserId == incomingRequester.Id);
        result.Value.OutgoingRequests.Should().ContainSingle(r => r.UserId == outgoingAddressee.Id);
    }

    [Fact]
    public async Task QueriesReaderCappedAtMaxFriends()
    {
        SocialTestHelpers.StubUsers(_userRepository, _caller);
        StubReader();

        await _handler.Handle(new GetFriendsQuery(_caller.Id), CancellationToken.None);

        await _reader.Received(1).ReadVisibleFriendshipsAsync(
            _caller.Id, AppConstants.MaxFriends, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FriendsOrderedByDisplayName()
    {
        var zed = SocialTestHelpers.OptedInUser("Zed");
        var amy = SocialTestHelpers.OptedInUser("Amy");
        SocialTestHelpers.StubUsers(_userRepository, _caller, zed, amy);
        StubReader(Accepted(_caller.Id, zed.Id), Accepted(_caller.Id, amy.Id));

        var result = await _handler.Handle(new GetFriendsQuery(_caller.Id), CancellationToken.None);

        result.Value.Friends.Select(f => f.DisplayName).Should().ContainInOrder("Amy", "Zed");
    }

    [Fact]
    public async Task DropsFriendshipWhoseOtherUserIsNotResolvable()
    {
        var deactivated = SocialTestHelpers.OptedInUser("Ghost");
        SocialTestHelpers.StubUsers(_userRepository, _caller);
        StubReader(Accepted(_caller.Id, deactivated.Id));

        var result = await _handler.Handle(new GetFriendsQuery(_caller.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Friends.Should().BeEmpty();
    }

    [Fact]
    public async Task CallerOptedOut_ReturnsSocialDisabled()
    {
        var optedOut = SocialTestHelpers.OptedOutUser();
        SocialTestHelpers.StubUsers(_userRepository, optedOut);

        var result = await _handler.Handle(new GetFriendsQuery(optedOut.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await _reader.DidNotReceive().ReadVisibleFriendshipsAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
