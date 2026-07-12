using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Social.Commands;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Social;

public class FriendshipCommandsTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepository = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<Notification> _notificationRepository = Substitute.For<IGenericRepository<Notification>>();
    private readonly IGenericRepository<XpAwardLog> _xpAwardLogRepository = Substitute.For<IGenericRepository<XpAwardLog>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IPushNotificationService _pushNotificationService = Substitute.For<IPushNotificationService>();

    private readonly SocialAccessGuard _guard;
    private readonly FriendGraphService _friendGraph;

    public FriendshipCommandsTests()
    {
        _guard = new SocialAccessGuard(_userRepository);
        _friendGraph = new FriendGraphService(_userRepository, _friendshipRepository, _blockedUserRepository);
        SocialTestHelpers.StubFind(_achievementRepository);
    }

    private SocialNotificationDispatcher Dispatcher() =>
        new(_notificationRepository, _pushNotificationService,
            Substitute.For<ILogger<SocialNotificationDispatcher>>());

    private SendFriendRequestCommandHandler SendHandler() =>
        new(_guard, _friendGraph, _friendshipRepository, Dispatcher(), _unitOfWork);

    private AcceptFriendRequestCommandHandler AcceptHandler() =>
        new(_guard, _friendshipRepository, _userRepository, _achievementRepository, Dispatcher(),
            new XpAwarder(_xpAwardLogRepository), _unitOfWork);

    [Fact]
    public async Task SendRequest_ValidHandle_CreatesPendingFriendship()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var target = SocialTestHelpers.OptedInUser();
        target.SetHandle("friendhandle");
        SocialTestHelpers.StubUsers(_userRepository, caller, target);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await SendHandler().Handle(
            new SendFriendRequestCommand(caller.Id, "friendhandle", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _friendshipRepository.Received(1).AddAsync(
            Arg.Is<Friendship>(f => f.RequesterId == caller.Id && f.AddresseeId == target.Id && f.Status == FriendshipStatus.Pending),
            Arg.Any<CancellationToken>());
        await _notificationRepository.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.UserId == target.Id && n.Url == "/social?tab=friends"),
            Arg.Any<CancellationToken>());
        await _pushNotificationService.Received(1).SendToUserAsync(
            target.Id, Arg.Any<string>(), Arg.Any<string>(), "/social?tab=friends", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendRequest_CallerOptedOut_ReturnsSocialDisabled()
    {
        var caller = SocialTestHelpers.OptedOutUser();
        SocialTestHelpers.StubUsers(_userRepository, caller);

        var result = await SendHandler().Handle(
            new SendFriendRequestCommand(caller.Id, "anyhandle", null), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.SocialDisabled);
    }

    [Fact]
    public async Task SendRequest_TargetNotFound_ReturnsUniformUserNotFound()
    {
        var caller = SocialTestHelpers.OptedInUser();
        SocialTestHelpers.StubUsers(_userRepository, caller);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await SendHandler().Handle(
            new SendFriendRequestCommand(caller.Id, "ghost", null), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task SendRequest_TargetOptedOut_ReturnsUniformUserNotFound()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var target = SocialTestHelpers.OptedOutUser();
        target.SetHandle("privatehandle");
        SocialTestHelpers.StubUsers(_userRepository, caller, target);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await SendHandler().Handle(
            new SendFriendRequestCommand(caller.Id, "privatehandle", null), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task SendRequest_ToSelf_ReturnsUniformUserNotFound()
    {
        var caller = SocialTestHelpers.OptedInUser();
        caller.SetHandle("myself");
        SocialTestHelpers.StubUsers(_userRepository, caller);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await SendHandler().Handle(
            new SendFriendRequestCommand(caller.Id, "myself", null), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task SendRequest_Blocked_ReturnsUniformUserNotFound()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var target = SocialTestHelpers.OptedInUser();
        target.SetHandle("blockedhandle");
        SocialTestHelpers.StubUsers(_userRepository, caller, target);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository, BlockedUser.Create(target.Id, caller.Id).Value);

        var result = await SendHandler().Handle(
            new SendFriendRequestCommand(caller.Id, "blockedhandle", null), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task SendRequest_AlreadyConnected_ReturnsAlreadyFriends()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var target = SocialTestHelpers.OptedInUser();
        target.SetHandle("existinghandle");
        SocialTestHelpers.StubUsers(_userRepository, caller, target);
        SocialTestHelpers.StubFind(_friendshipRepository, Friendship.Create(caller.Id, target.Id).Value);
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await SendHandler().Handle(
            new SendFriendRequestCommand(caller.Id, "existinghandle", null), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.AlreadyFriends);
    }

    [Fact]
    public async Task SendRequest_AtFriendCap_ReturnsFriendLimitReached()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var target = SocialTestHelpers.OptedInUser();
        target.SetHandle("caphandle");
        SocialTestHelpers.StubUsers(_userRepository, caller, target);
        SocialTestHelpers.StubFind(_friendshipRepository);
        SocialTestHelpers.StubFind(_blockedUserRepository);
        _friendshipRepository.CountAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<Friendship, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(AppConstants.MaxFriends);

        var result = await SendHandler().Handle(
            new SendFriendRequestCommand(caller.Id, "caphandle", null), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.FriendLimitReached);
    }

    [Fact]
    public async Task Accept_ByAddressee_SetsAcceptedAndPushesRequester()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var requester = SocialTestHelpers.OptedInUser();
        var friendship = Friendship.Create(requester.Id, caller.Id).Value;
        SocialTestHelpers.StubUsers(_userRepository, caller, requester);
        SocialTestHelpers.StubFind(_friendshipRepository, friendship);

        var result = await AcceptHandler().Handle(
            new AcceptFriendRequestCommand(caller.Id, friendship.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        friendship.Status.Should().Be(FriendshipStatus.Accepted);
        friendship.RespondedAtUtc.Should().NotBeNull();
        await _notificationRepository.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.UserId == requester.Id && n.Url == "/social?tab=friends"),
            Arg.Any<CancellationToken>());
        await _pushNotificationService.Received(1).SendToUserAsync(
            requester.Id, Arg.Any<string>(), Arg.Any<string>(), "/social?tab=friends", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Accept_UnknownRequest_ReturnsFriendRequestNotFound()
    {
        var caller = SocialTestHelpers.OptedInUser();
        SocialTestHelpers.StubUsers(_userRepository, caller);
        SocialTestHelpers.StubFind(_friendshipRepository);

        var result = await AcceptHandler().Handle(
            new AcceptFriendRequestCommand(caller.Id, Guid.NewGuid()), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.FriendRequestNotFound);
    }

    [Fact]
    public async Task Accept_ByNonAddressee_ReturnsFriendRequestNotFound()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var requester = SocialTestHelpers.OptedInUser();
        var someoneElse = SocialTestHelpers.OptedInUser();
        var friendship = Friendship.Create(requester.Id, someoneElse.Id).Value;
        SocialTestHelpers.StubUsers(_userRepository, caller, requester, someoneElse);
        SocialTestHelpers.StubFind(_friendshipRepository, friendship);

        var result = await AcceptHandler().Handle(
            new AcceptFriendRequestCommand(caller.Id, friendship.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.FriendRequestNotFound);
    }

    [Fact]
    public async Task Accept_AlreadyAccepted_ReturnsFriendshipNotPending()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var requester = SocialTestHelpers.OptedInUser();
        var friendship = Friendship.Create(requester.Id, caller.Id).Value;
        friendship.Accept();
        SocialTestHelpers.StubUsers(_userRepository, caller, requester);
        SocialTestHelpers.StubFind(_friendshipRepository, friendship);

        var result = await AcceptHandler().Handle(
            new AcceptFriendRequestCommand(caller.Id, friendship.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("FRIENDSHIP_NOT_PENDING");
    }

    [Fact]
    public async Task Accept_FirstAcceptedFriendship_EvaluatesFriendCountAwardsForBothParties()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var requester = SocialTestHelpers.OptedInUser();
        var friendship = Friendship.Create(requester.Id, caller.Id).Value;
        SocialTestHelpers.StubUsers(_userRepository, caller, requester);
        SocialTestHelpers.StubFind(_friendshipRepository, friendship);

        var result = await AcceptHandler().Handle(
            new AcceptFriendRequestCommand(caller.Id, friendship.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        friendship.Status.Should().Be(FriendshipStatus.Accepted);
        await _friendshipRepository.Received(2).CountAsync(
            Arg.Any<Expression<Func<Friendship, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Accept_AtSquadGoalsThreshold_EvaluatesAwardsAndSucceeds()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var requester = SocialTestHelpers.OptedInUser();
        var friendship = Friendship.Create(requester.Id, caller.Id).Value;
        SocialTestHelpers.StubUsers(_userRepository, caller, requester);
        SocialTestHelpers.StubFind(_friendshipRepository, friendship);
        _friendshipRepository.CountAsync(
                Arg.Any<Expression<Func<Friendship, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(5);

        var result = await AcceptHandler().Handle(
            new AcceptFriendRequestCommand(caller.Id, friendship.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _achievementRepository.Received().FindAsync(
            Arg.Any<Expression<Func<UserAchievement, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_ExistingFriendship_DeletesRow()
    {
        var caller = SocialTestHelpers.OptedInUser();
        var friend = SocialTestHelpers.OptedInUser();
        var friendship = Friendship.Create(caller.Id, friend.Id).Value;
        friendship.Accept();
        SocialTestHelpers.StubUsers(_userRepository, caller, friend);
        SocialTestHelpers.StubFind(_friendshipRepository, friendship);

        var handler = new RemoveFriendCommandHandler(_guard, _friendGraph, _friendshipRepository, _unitOfWork);
        var result = await handler.Handle(new RemoveFriendCommand(caller.Id, friend.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _friendshipRepository.Received(1).Remove(friendship);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_NoFriendship_IsNoOpSuccess()
    {
        var caller = SocialTestHelpers.OptedInUser();
        SocialTestHelpers.StubUsers(_userRepository, caller);
        SocialTestHelpers.StubFind(_friendshipRepository);

        var handler = new RemoveFriendCommandHandler(_guard, _friendGraph, _friendshipRepository, _unitOfWork);
        var result = await handler.Handle(new RemoveFriendCommand(caller.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _friendshipRepository.DidNotReceive().Remove(Arg.Any<Friendship>());
    }
}
