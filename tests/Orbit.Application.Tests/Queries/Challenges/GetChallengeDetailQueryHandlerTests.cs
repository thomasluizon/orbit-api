using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Challenges.Queries;
using Orbit.Application.Common;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Challenges;

public class GetChallengeDetailQueryHandlerTests
{
    private readonly IGenericRepository<Challenge> _challengeRepository = Substitute.For<IGenericRepository<Challenge>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepository = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetChallengeDetailQueryHandler _handler;

    private readonly User _creator = SocialTestHelpers.OptedInUser("Creator");
    private readonly User _member = SocialTestHelpers.OptedInUser("Member");
    private readonly Guid _habitA = Guid.NewGuid();
    private readonly Guid _habitB = Guid.NewGuid();

    private static readonly DateOnly Today = new(2026, 3, 15);
    private static readonly DateOnly PeriodStart = new(2026, 3, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 3, 31);

    public GetChallengeDetailQueryHandlerTests()
    {
        _handler = new GetChallengeDetailQueryHandler(
            _challengeRepository, _habitLogRepository, _userRepository, _userDateService);

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        SocialTestHelpers.StubUsers(_userRepository, _creator, _member);
    }

    private Challenge BuildChallenge(ChallengeType type, int? target, DateOnly? periodEnd)
    {
        var challenge = Challenge.Create(new CreateChallengeParams(
            _creator.Id, type, "Challenge", null, target, PeriodStart, periodEnd, "ABC23456")).Value;
        challenge.AddParticipant(_creator.Id, [_habitA]);
        challenge.AddParticipant(_member.Id, [_habitB]);
        return challenge;
    }

    private void StubChallenge(Challenge challenge)
    {
        _challengeRepository.FindAsync(
                Arg.Any<Expression<Func<Challenge, bool>>>(),
                Arg.Any<Func<IQueryable<Challenge>, IQueryable<Challenge>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Challenge> { challenge }.AsReadOnly());
    }

    private void StubLogs(params HabitLog[] logs)
    {
        _habitLogRepository.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(logs.ToList().AsReadOnly());
    }

    [Fact]
    public async Task CoopGoal_ReachingTarget_ReportsCompleteWithSummedProgress()
    {
        StubChallenge(BuildChallenge(ChallengeType.CoopGoal, target: 2, PeriodEnd));
        StubLogs(HabitLog.Create(_habitA, Today, 1), HabitLog.Create(_habitB, Today, 1));

        var result = await _handler.Handle(new GetChallengeDetailQuery(_creator.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentProgress.Should().Be(2);
        result.Value.IsComplete.Should().BeTrue();
        result.Value.Participants.Should().HaveCount(2);
    }

    [Fact]
    public async Task CoopGoal_BelowTarget_ReportsIncomplete()
    {
        StubChallenge(BuildChallenge(ChallengeType.CoopGoal, target: 5, PeriodEnd));
        StubLogs(HabitLog.Create(_habitA, Today, 1));

        var result = await _handler.Handle(new GetChallengeDetailQuery(_creator.Id, Guid.NewGuid()), CancellationToken.None);

        result.Value.CurrentProgress.Should().Be(1);
        result.Value.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task StreakTogether_AdvancesWhileAllParticipantsLog()
    {
        StubChallenge(BuildChallenge(ChallengeType.StreakTogether, target: null, periodEnd: null));
        StubLogs(
            HabitLog.Create(_habitA, Today, 1), HabitLog.Create(_habitB, Today, 1),
            HabitLog.Create(_habitA, Today.AddDays(-1), 1), HabitLog.Create(_habitB, Today.AddDays(-1), 1),
            HabitLog.Create(_habitA, Today.AddDays(-2), 1), HabitLog.Create(_habitB, Today.AddDays(-2), 1));

        var result = await _handler.Handle(new GetChallengeDetailQuery(_member.Id, Guid.NewGuid()), CancellationToken.None);

        result.Value.CurrentProgress.Should().Be(3);
        result.Value.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task StreakTogether_ResetsAfterOneParticipantMisses()
    {
        StubChallenge(BuildChallenge(ChallengeType.StreakTogether, target: null, periodEnd: null));
        StubLogs(
            HabitLog.Create(_habitA, Today, 1), HabitLog.Create(_habitB, Today, 1),
            HabitLog.Create(_habitA, Today.AddDays(-1), 1));

        var result = await _handler.Handle(new GetChallengeDetailQuery(_creator.Id, Guid.NewGuid()), CancellationToken.None);

        result.Value.CurrentProgress.Should().Be(1);
    }

    [Fact]
    public async Task ProgressReadFilter_CountsEveryParticipantHabitButExcludesUnlinkedHabits()
    {
        StubChallenge(BuildChallenge(ChallengeType.CoopGoal, target: 5, PeriodEnd));

        Expression<Func<HabitLog, bool>>? readFilter = null;
        _habitLogRepository.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                readFilter = call.Arg<Expression<Func<HabitLog, bool>>>();
                return (IReadOnlyList<HabitLog>)new List<HabitLog>();
            });

        await _handler.Handle(new GetChallengeDetailQuery(_creator.Id, Guid.NewGuid()), CancellationToken.None);

        readFilter.Should().NotBeNull();
        var matches = readFilter!.Compile();
        matches(HabitLog.Create(_habitA, Today, 1)).Should().BeTrue("the caller's linked habit contributes to shared progress");
        matches(HabitLog.Create(_habitB, Today, 1)).Should().BeTrue("every participant's linked habit contributes to shared progress");
        matches(HabitLog.Create(Guid.NewGuid(), Today, 1)).Should().BeFalse("a habit linked to no participant must be excluded");
    }

    [Fact]
    public async Task NonParticipant_ReturnsChallengeNotFound()
    {
        StubChallenge(BuildChallenge(ChallengeType.CoopGoal, target: 2, PeriodEnd));
        StubLogs();

        var result = await _handler.Handle(new GetChallengeDetailQuery(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ChallengeNotFound);
    }
}
