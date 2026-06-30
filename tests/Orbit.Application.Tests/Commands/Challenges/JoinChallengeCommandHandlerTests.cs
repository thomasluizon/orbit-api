using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Common;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Social.Services;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Challenges;

public class JoinChallengeCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Friendship> _friendshipRepository = Substitute.For<IGenericRepository<Friendship>>();
    private readonly IGenericRepository<BlockedUser> _blockedUserRepository = Substitute.For<IGenericRepository<BlockedUser>>();
    private readonly IGenericRepository<Challenge> _challengeRepository = Substitute.For<IGenericRepository<Challenge>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<XpAwardLog> _xpAwardLogRepository = Substitute.For<IGenericRepository<XpAwardLog>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly JoinChallengeCommandHandler _handler;

    private readonly User _joiner = SocialTestHelpers.OptedInUser("Joiner");
    private readonly Guid _creatorId = Guid.NewGuid();
    private readonly Guid _joinerHabitId = Guid.NewGuid();
    private const string Code = "ABC23456";

    public JoinChallengeCommandHandlerTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        var friendGraph = new FriendGraphService(_userRepository, _friendshipRepository, _blockedUserRepository);
        _handler = new JoinChallengeCommandHandler(
            guard, friendGraph, _challengeRepository, _habitRepository, _achievementRepository,
            new XpAwarder(_xpAwardLogRepository), _unitOfWork);

        SocialTestHelpers.StubUsers(_userRepository, _joiner);
        SocialTestHelpers.StubFind(_blockedUserRepository);
        SocialTestHelpers.StubFind(_achievementRepository);
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(1);
        StubChallengeLookup(ActiveChallenge());
    }

    private Challenge ActiveChallenge()
    {
        var challenge = Challenge.Create(new CreateChallengeParams(
            _creatorId, ChallengeType.CoopGoal, "Challenge", null, 30,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), Code)).Value;
        challenge.AddParticipant(_creatorId, [Guid.NewGuid()]);
        return challenge;
    }

    private void StubChallengeLookup(Challenge? challenge)
    {
        _challengeRepository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Challenge, bool>>>(),
                Arg.Any<Func<IQueryable<Challenge>, IQueryable<Challenge>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(challenge);
    }

    private JoinChallengeCommand Command() => new(_joiner.Id, Code, [_joinerHabitId]);

    [Fact]
    public async Task ValidJoin_AddsParticipantAndSaves()
    {
        var challenge = ActiveChallenge();
        StubChallengeLookup(challenge);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        challenge.Participants.Should().Contain(p => p.UserId == _joiner.Id && p.IsActive);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidJoin_GrantsTeamPlayer()
    {
        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _achievementRepository.Received().AddAsync(
            Arg.Is<UserAchievement>(a => a.AchievementId == "team_player"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownCode_ReturnsInvalidJoinCode()
    {
        StubChallengeLookup(null);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.InvalidJoinCode);
    }

    [Fact]
    public async Task CompletedChallenge_ReturnsChallengeClosed()
    {
        var challenge = ActiveChallenge();
        challenge.MarkCompleted();
        StubChallengeLookup(challenge);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ChallengeClosed);
    }

    [Fact]
    public async Task AlreadyActiveParticipant_ReturnsAlreadyJoined()
    {
        var challenge = ActiveChallenge();
        challenge.AddParticipant(_joiner.Id, [_joinerHabitId]);
        StubChallengeLookup(challenge);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.AlreadyJoinedChallenge);
    }

    [Fact]
    public async Task BlockedByCreator_ReturnsInvalidJoinCode()
    {
        SocialTestHelpers.StubFind(_blockedUserRepository, BlockedUser.Create(_creatorId, _joiner.Id).Value);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.InvalidJoinCode);
    }

    [Fact]
    public async Task FullChallenge_ReturnsChallengeFull()
    {
        var challenge = ActiveChallenge();
        for (var i = challenge.GetActiveParticipants().Count; i < AppConstants.MaxChallengeParticipants; i++)
            challenge.AddParticipant(Guid.NewGuid(), [Guid.NewGuid()]);
        StubChallengeLookup(challenge);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ChallengeFull);
    }

    [Fact]
    public async Task HabitNotOwned_ReturnsHabitNotFound()
    {
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(0);

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }
}
