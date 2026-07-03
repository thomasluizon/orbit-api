using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class GetInvitePreviewQueryTests
{
    private const string OwnerCode = "ABCD2345";

    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepository = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();

    private readonly SocialAccessGuard _guard;
    private readonly FriendGraphService _friendGraph;

    public GetInvitePreviewQueryTests()
    {
        _guard = new SocialAccessGuard(_userRepository);
        _friendGraph = new FriendGraphService(_userRepository, _friendshipRepository, _blockedUserRepository);
    }

    private GetInvitePreviewQueryHandler Handler() => new(_guard, _friendGraph);

    private static User OwnerWithCode(string name = "Ada Lovelace")
    {
        var owner = SocialTestHelpers.OptedInUser(name);
        owner.SetReferralCode(OwnerCode);
        return owner;
    }

    [Fact]
    public async Task ValidCode_NoRelationship_ReturnsOwnerWithAllFlagsFalse()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var owner = OwnerWithCode();
        SocialTestHelpers.StubUsers(_userRepository, caller, owner);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await Handler().Handle(new GetInvitePreviewQuery(caller.Id, OwnerCode), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Handle.Should().Be(owner.Handle);
        result.Value.DisplayName.Should().Be(owner.Name);
        result.Value.IsSelf.Should().BeFalse();
        result.Value.IsAlreadyFriend.Should().BeFalse();
        result.Value.HasPendingRequest.Should().BeFalse();
    }

    [Fact]
    public async Task OwnCode_ReturnsIsSelf()
    {
        var caller = OwnerWithCode();
        SocialTestHelpers.StubUsers(_userRepository, caller);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await Handler().Handle(new GetInvitePreviewQuery(caller.Id, OwnerCode), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsSelf.Should().BeTrue();
        result.Value.IsAlreadyFriend.Should().BeFalse();
        result.Value.HasPendingRequest.Should().BeFalse();
    }

    [Fact]
    public async Task AcceptedFriendship_ReturnsIsAlreadyFriend()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var owner = OwnerWithCode();
        var friendship = Friendship.Create(caller.Id, owner.Id).Value;
        friendship.Accept();
        SocialTestHelpers.StubUsers(_userRepository, caller, owner);
        SocialTestHelpers.StubFind(_friendshipRepository, friendship);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await Handler().Handle(new GetInvitePreviewQuery(caller.Id, OwnerCode), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsSelf.Should().BeFalse();
        result.Value.IsAlreadyFriend.Should().BeTrue();
        result.Value.HasPendingRequest.Should().BeFalse();
    }

    [Fact]
    public async Task PendingFriendshipEitherDirection_ReturnsHasPendingRequest()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var owner = OwnerWithCode();
        var friendship = Friendship.Create(owner.Id, caller.Id).Value;
        SocialTestHelpers.StubUsers(_userRepository, caller, owner);
        SocialTestHelpers.StubFind(_friendshipRepository, friendship);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await Handler().Handle(new GetInvitePreviewQuery(caller.Id, OwnerCode), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasPendingRequest.Should().BeTrue();
        result.Value.IsAlreadyFriend.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownCode_ReturnsUserNotFound()
    {
        var caller = SocialTestHelpers.OptedInUser();
        SocialTestHelpers.StubUsers(_userRepository, caller);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await Handler().Handle(new GetInvitePreviewQuery(caller.Id, OwnerCode), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task MalformedCode_ReturnsUserNotFound()
    {
        var caller = SocialTestHelpers.OptedInUser();
        SocialTestHelpers.StubUsers(_userRepository, caller);

        var result = await Handler().Handle(new GetInvitePreviewQuery(caller.Id, "abc"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task CallerOptedOut_ReturnsSocialDisabled()
    {
        var caller = SocialTestHelpers.OptedOutUser();
        SocialTestHelpers.StubUsers(_userRepository, caller);

        var result = await Handler().Handle(new GetInvitePreviewQuery(caller.Id, OwnerCode), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.SocialDisabled);
    }

    [Fact]
    public async Task OwnerOptedOut_ReturnsUserNotFound()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var owner = SocialTestHelpers.OptedOutUser("Private");
        owner.SetReferralCode(OwnerCode);
        SocialTestHelpers.StubUsers(_userRepository, caller, owner);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await Handler().Handle(new GetInvitePreviewQuery(caller.Id, OwnerCode), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }
}
