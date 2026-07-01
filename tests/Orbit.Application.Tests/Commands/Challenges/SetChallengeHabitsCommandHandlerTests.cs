using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Challenges;

public class SetChallengeHabitsCommandHandlerTests
{
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Challenge> _challengeRepository = Substitute.For<IGenericRepository<Challenge>>();
    private readonly IGenericRepository<Habit> _habitRepository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly SetChallengeHabitsCommandHandler _handler;

    private readonly User _creator = SocialTestHelpers.OptedInUser("Creator");
    private readonly User _member = SocialTestHelpers.OptedInUser("Member");
    private readonly Challenge _challenge;
    private readonly Guid _newHabitId = Guid.NewGuid();

    public SetChallengeHabitsCommandHandlerTests()
    {
        var guard = new SocialAccessGuard(_userRepository);
        _handler = new SetChallengeHabitsCommandHandler(guard, _challengeRepository, _habitRepository, _unitOfWork);

        _challenge = Challenge.Create(new CreateChallengeParams(
            _creator.Id, ChallengeType.CoopGoal, "Challenge", null, 30,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), "ABC23456")).Value;
        _challenge.AddParticipant(_creator.Id, [Guid.NewGuid()]);
        _challenge.AddParticipant(_member.Id, [Guid.NewGuid()]);

        SocialTestHelpers.StubUsers(_userRepository, _creator, _member);
        StubChallenge(_challenge);
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(1);
    }

    private void StubChallenge(Challenge? challenge)
    {
        _challengeRepository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Challenge, bool>>>(),
                Arg.Any<Func<IQueryable<Challenge>, IQueryable<Challenge>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(challenge);
    }

    private SetChallengeHabitsCommand Command(Guid userId) => new(userId, _challenge.Id, new[] { _newHabitId });

    [Fact]
    public async Task ActiveParticipant_ReplacesLinkedHabitsAndSaves()
    {
        var result = await _handler.Handle(Command(_member.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var participant = _challenge.Participants.Single(p => p.UserId == _member.Id);
        participant.LinkedHabits.Should().ContainSingle(h => h.HabitId == _newHabitId);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitNotOwnedByCaller_ReturnsHabitNotFound()
    {
        _habitRepository.CountAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>()).Returns(0);

        var result = await _handler.Handle(Command(_member.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonParticipant_ReturnsNotChallengeParticipant()
    {
        var stranger = SocialTestHelpers.OptedInUser("Stranger");
        SocialTestHelpers.StubUsers(_userRepository, _creator, _member, stranger);

        var result = await _handler.Handle(Command(stranger.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.NotChallengeParticipant);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LeftParticipant_ReturnsNotChallengeParticipant()
    {
        _challenge.TryLeave(_member.Id);

        var result = await _handler.Handle(Command(_member.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.NotChallengeParticipant);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChallengeNotFound_ReturnsChallengeNotFound()
    {
        StubChallenge(null);

        var result = await _handler.Handle(Command(_member.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ChallengeNotFound);
    }

    [Fact]
    public async Task SocialDisabled_ReturnsSocialDisabled()
    {
        var optedOut = SocialTestHelpers.OptedOutUser("Private");
        SocialTestHelpers.StubUsers(_userRepository, optedOut);

        var result = await _handler.Handle(Command(optedOut.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.SocialDisabled);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
