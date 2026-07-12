using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Challenges;

public class CreateChallengeCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepository = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IGenericRepository<Challenge> _challengeRepository = Substitute.For<IGenericRepository<Challenge>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IPushNotificationService _push = Substitute.For<IPushNotificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly CreateChallengeCommandHandler _handler;

    private readonly User _creator = SocialTestHelpers.OptedInUser("Creator");
    private readonly User _friend = SocialTestHelpers.OptedInUser("Friend");
    private readonly Guid _habitId = Guid.NewGuid();

    private static readonly DateOnly PeriodStart = new(2026, 3, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 3, 31);

    public CreateChallengeCommandHandlerTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var friendGraph = new FriendGraphService(_userRepository, _friendshipRepository, _blockedUserRepository);
        var scopeFactory = new ServiceCollection()
            .AddSingleton(_push)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        _handler = new CreateChallengeCommandHandler(
            guard, friendGraph, _challengeRepository, _habitRepository, _userRepository,
            scopeFactory, _unitOfWork, Substitute.For<ILogger<CreateChallengeCommandHandler>>());

        SocialTestHelpers.StubUsers(_userRepository, _creator, _friend);
        SocialTestHelpers.StubFind(_friendshipRepository, AcceptedFriendship());
        SocialTestHelpers.StubFind(_blockedUserRepository);
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(1);
        _challengeRepository.AnyAsync(Arg.Any<Expression<Func<Challenge, bool>>>(), Arg.Any<CancellationToken>()).Returns(false);
    }

    private Friendship AcceptedFriendship()
    {
        var friendship = Friendship.Create(_creator.Id, _friend.Id).Value;
        friendship.Accept();
        return friendship;
    }

    private CreateChallengeCommand Command(IReadOnlyList<Guid>? invited = null) =>
        new(_creator.Id, ChallengeType.CoopGoal, "March Challenge", null, 30, PeriodStart, PeriodEnd,
            [_habitId], invited ?? [_friend.Id]);

    [Fact]
    public async Task ValidCommand_PersistsChallengeWithCreatorAndInvitedFriend()
    {
        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _challengeRepository.Received(1).AddAsync(
            Arg.Is<Challenge>(c =>
                c.CreatorId == _creator.Id
                && c.Participants.Count == 2
                && c.Participants.Any(p => p.UserId == _creator.Id && p.LinkedHabits.Count == 1)
                && c.Participants.Any(p => p.UserId == _friend.Id)),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidCommand_PushesEachInvitedFriendOnce()
    {
        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _push.Received(1).SendToUserAsync(
            _friend.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManyInvitedFriends_PushesEachExactlyOnce_AcrossThrottledFanOut()
    {
        var friends = Enumerable.Range(0, 8)
            .Select(index => SocialTestHelpers.OptedInUser($"Friend-{index}"))
            .ToList();

        SocialTestHelpers.StubUsers(_userRepository, [_creator, .. friends]);
        SocialTestHelpers.StubFind(_friendshipRepository, friends.Select(AcceptedFriendshipWith).ToArray());
        SocialTestHelpers.StubFind(_blockedUserRepository);

        var result = await _handler.Handle(Command(invited: friends.Select(f => f.Id).ToList()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        foreach (var friend in friends)
        {
            await _push.Received(1).SendToUserAsync(
                friend.Id, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        await _push.Received(friends.Count).SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private Friendship AcceptedFriendshipWith(User friend)
    {
        var friendship = Friendship.Create(_creator.Id, friend.Id).Value;
        friendship.Accept();
        return friendship;
    }

    [Fact]
    public async Task NoInvitedFriends_DoesNotPush()
    {
        var result = await _handler.Handle(Command(invited: []), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _push.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushThrows_ChallengeStillCreated()
    {
        _push.SendToUserAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("push down")));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _challengeRepository.Received(1).AddAsync(Arg.Any<Challenge>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SocialDisabled_ReturnsSocialDisabled()
    {
        var optedOut = SocialTestHelpers.OptedOutUser("Private");
        SocialTestHelpers.StubUsers(_userRepository, optedOut);

        var result = await _handler.Handle(
            new CreateChallengeCommand(optedOut.Id, ChallengeType.CoopGoal, "X", null, 5, PeriodStart, PeriodEnd, [_habitId], []),
            CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.SocialDisabled);
        await _challengeRepository.DidNotReceive().AddAsync(Arg.Any<Challenge>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitNotOwned_ReturnsHabitNotFound()
    {
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(0);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
        await _challengeRepository.DidNotReceive().AddAsync(Arg.Any<Challenge>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonFriendInvite_ReturnsNotFriends()
    {
        SocialTestHelpers.StubFind(_friendshipRepository);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.NotFriends);
        await _challengeRepository.DidNotReceive().AddAsync(Arg.Any<Challenge>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockedInvitee_ReturnsNotFriends()
    {
        SocialTestHelpers.StubFind(_blockedUserRepository, BlockedUser.Create(_friend.Id, _creator.Id).Value);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.NotFriends);
    }

    [Fact]
    public async Task InvitingOverParticipantCap_ReturnsChallengeFull()
    {
        var tooManyFriends = Enumerable.Range(0, AppConstants.MaxChallengeParticipants).Select(_ => Guid.NewGuid()).ToList();

        var result = await _handler.Handle(Command(invited: tooManyFriends), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ChallengeFull);
        await _challengeRepository.DidNotReceive().AddAsync(Arg.Any<Challenge>(), Arg.Any<CancellationToken>());
    }
}
