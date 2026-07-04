using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Accountability.Commands;
using Orbit.Application.Accountability.Services;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Accountability;

public class InviteAccountabilityBuddyCommandTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepository = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IGenericRepository<AccountabilityPair> _pairRepository = Substitute.For<IGenericRepository<AccountabilityPair>>();
    private readonly IGenericRepository<AccountabilityPairHabit> _pairHabitRepository = Substitute.For<IGenericRepository<AccountabilityPairHabit>>();
    private readonly IGenericRepository<AccountabilityCheckIn> _checkInRepository = Substitute.For<IGenericRepository<AccountabilityCheckIn>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Notification> _notificationRepository = Substitute.For<IGenericRepository<Notification>>();
    private readonly IPushNotificationService _push = Substitute.For<IPushNotificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly InviteAccountabilityBuddyCommandHandler _handler;

    private readonly User _caller = SocialTestHelpers.OptedInUser("Caller");
    private readonly User _buddy = SocialTestHelpers.OptedInUser("Buddy");
    private readonly Guid _habitId = Guid.NewGuid();

    public InviteAccountabilityBuddyCommandTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var friendGraph = new FriendGraphService(_userRepository, _friendshipRepository, _blockedUserRepository);
        var pairService = new AccountabilityPairService(_pairRepository, _pairHabitRepository, _habitRepository);
        var repositories = new AccountabilityRepositories(_userRepository, _pairRepository, _checkInRepository, _achievementRepository);
        var dispatcher = new SocialNotificationDispatcher(
            _notificationRepository, _push, Substitute.For<ILogger<SocialNotificationDispatcher>>());
        _handler = new InviteAccountabilityBuddyCommandHandler(
            guard, friendGraph, pairService, repositories, dispatcher, _unitOfWork);

        SocialTestHelpers.StubUsers(_userRepository, _caller, _buddy);
        AccountabilityTestHelpers.StubFind(_friendshipRepository, AcceptedFriendship());
        AccountabilityTestHelpers.StubFind(_blockedUserRepository);
        AccountabilityTestHelpers.StubFind(_pairRepository);
        AccountabilityTestHelpers.StubFind(_pairHabitRepository);
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(1);
    }

    private Friendship AcceptedFriendship()
    {
        var friendship = Friendship.Create(_caller.Id, _buddy.Id).Value;
        friendship.Accept();
        return friendship;
    }

    private InviteAccountabilityBuddyCommand Command() =>
        new(_caller.Id, _buddy.Id, AccountabilityCadence.Weekly, new[] { _habitId });

    [Fact]
    public async Task Friends_CreatesPendingPair_LinksHabits_AndPushesBuddy()
    {
        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _pairRepository.Received(1).AddAsync(
            Arg.Is<AccountabilityPair>(p =>
                p.RequesterId == _caller.Id && p.AddresseeId == _buddy.Id
                && p.Status == AccountabilityPairStatus.Pending && p.Cadence == AccountabilityCadence.Weekly),
            Arg.Any<CancellationToken>());
        await _pairHabitRepository.Received(1).AddAsync(
            Arg.Is<AccountabilityPairHabit>(ph => ph.UserId == _caller.Id && ph.HabitId == _habitId),
            Arg.Any<CancellationToken>());
        await _notificationRepository.Received(1).AddAsync(
            Arg.Is<Notification>(n => n.UserId == _buddy.Id && n.Url == "/social?tab=buddies"),
            Arg.Any<CancellationToken>());
        await _push.Received(1).SendToUserAsync(
            _buddy.Id, Arg.Any<string>(), Arg.Any<string>(), "/social?tab=buddies", Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CallerOptedOut_ReturnsSocialDisabled()
    {
        var caller = SocialTestHelpers.OptedOutUser("Private");
        SocialTestHelpers.StubUsers(_userRepository, caller, _buddy);

        var result = await _handler.Handle(
            new InviteAccountabilityBuddyCommand(caller.Id, _buddy.Id, AccountabilityCadence.Daily, new[] { _habitId }),
            CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.SocialDisabled);
    }

    [Fact]
    public async Task BuddyOptedOut_ReturnsNotFriends()
    {
        var buddy = SocialTestHelpers.OptedOutUser("PrivateBuddy");
        SocialTestHelpers.StubUsers(_userRepository, _caller, buddy);

        var result = await _handler.Handle(
            new InviteAccountabilityBuddyCommand(_caller.Id, buddy.Id, AccountabilityCadence.Daily, new[] { _habitId }),
            CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.NotFriends);
    }

    [Fact]
    public async Task NotAcceptedFriends_ReturnsNotFriends()
    {
        AccountabilityTestHelpers.StubFind(_friendshipRepository);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.NotFriends);
        await _pairRepository.DidNotReceive().AddAsync(Arg.Any<AccountabilityPair>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Blocked_ReturnsBlocked()
    {
        AccountabilityTestHelpers.StubFind(_blockedUserRepository, BlockedUser.Create(_buddy.Id, _caller.Id).Value);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.Blocked);
    }

    [Fact]
    public async Task ExistingActivePair_ReturnsAlreadyPaired()
    {
        var existing = AccountabilityPair.Create(_caller.Id, _buddy.Id, AccountabilityCadence.Daily).Value;
        AccountabilityTestHelpers.StubFind(_pairRepository, existing);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.AlreadyPaired);
        await _pairRepository.DidNotReceive().AddAsync(Arg.Any<AccountabilityPair>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AtPairCap_ReturnsPairLimitReached()
    {
        _pairRepository.CountAsync(Arg.Any<Expression<Func<AccountabilityPair, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(AppConstants.MaxAccountabilityPairs);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.PairLimitReached);
    }

    [Fact]
    public async Task HabitNotOwnedByCaller_ReturnsHabitNotFound()
    {
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(0);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
