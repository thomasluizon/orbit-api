using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Challenges;

public class LeaveChallengeCommandHandlerTests
{
    private readonly IGenericRepository<Challenge> _challengeRepository = Substitute.For<IGenericRepository<Challenge>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly LeaveChallengeCommandHandler _handler;

    private readonly Guid _creatorId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public LeaveChallengeCommandHandlerTests()
    {
        _handler = new LeaveChallengeCommandHandler(_challengeRepository, _unitOfWork);
    }

    private Challenge ChallengeWithMembers()
    {
        var challenge = Challenge.Create(new CreateChallengeParams(
            _creatorId, ChallengeType.CoopGoal, "Challenge", null, 30,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), "ABC23456")).Value;
        challenge.AddParticipant(_creatorId, [Guid.NewGuid()]);
        challenge.AddParticipant(_memberId, [Guid.NewGuid()]);
        return challenge;
    }

    private void StubLookup(Challenge? challenge)
    {
        _challengeRepository.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Challenge, bool>>>(),
                Arg.Any<Func<IQueryable<Challenge>, IQueryable<Challenge>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(challenge);
    }

    [Fact]
    public async Task ActiveParticipant_LeavesAndSaves()
    {
        var challenge = ChallengeWithMembers();
        StubLookup(challenge);

        var result = await _handler.Handle(new LeaveChallengeCommand(_memberId, challenge.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        challenge.Participants.Single(p => p.UserId == _memberId).IsActive.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatorLeaves_ChallengeRemainsActive()
    {
        var challenge = ChallengeWithMembers();
        StubLookup(challenge);

        var result = await _handler.Handle(new LeaveChallengeCommand(_creatorId, challenge.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        challenge.Status.Should().Be(ChallengeStatus.Active);
        challenge.Participants.Single(p => p.UserId == _creatorId).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task NonParticipant_ReturnsNotChallengeParticipant()
    {
        var challenge = ChallengeWithMembers();
        StubLookup(challenge);

        var result = await _handler.Handle(new LeaveChallengeCommand(Guid.NewGuid(), challenge.Id), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.NotChallengeParticipant);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChallengeNotFound_ReturnsChallengeNotFound()
    {
        StubLookup(null);

        var result = await _handler.Handle(new LeaveChallengeCommand(_memberId, Guid.NewGuid()), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ChallengeNotFound);
    }
}
