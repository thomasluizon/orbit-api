using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Challenges.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Challenges;

public class GetUserChallengesQueryHandlerTests
{
    private readonly IGenericRepository<Challenge> _challengeRepository = Substitute.For<IGenericRepository<Challenge>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepository = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetUserChallengesQueryHandler _handler;

    private readonly Guid _callerId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _callerHabit = Guid.NewGuid();
    private readonly Guid _memberHabit = Guid.NewGuid();

    private static readonly DateOnly Today = new(2026, 3, 15);
    private static readonly DateOnly PeriodStart = new(2026, 3, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 3, 31);

    public GetUserChallengesQueryHandlerTests()
    {
        _handler = new GetUserChallengesQueryHandler(_challengeRepository, _habitLogRepository, _userDateService);
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
    }

    private Challenge BuildChallenge(ChallengeType type, int? target, DateOnly? periodEnd, bool callerLinksHabit = true)
    {
        var challenge = Challenge.Create(new CreateChallengeParams(
            _callerId, type, "Challenge", null, target, PeriodStart, periodEnd,
            Guid.NewGuid().ToString("N")[..8])).Value;
        challenge.AddParticipant(_callerId, callerLinksHabit ? [_callerHabit] : []);
        challenge.AddParticipant(_memberId, [_memberHabit]);
        return challenge;
    }

    private void StubChallenges(params Challenge[] challenges)
    {
        _challengeRepository.FindAsync(
                Arg.Any<Expression<Func<Challenge, bool>>>(),
                Arg.Any<Func<IQueryable<Challenge>, IQueryable<Challenge>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<Challenge>)challenges
                .Where(call.Arg<Expression<Func<Challenge, bool>>>().Compile())
                .ToList());
    }

    private void StubLogs(params HabitLog[] logs)
    {
        _habitLogRepository.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(logs.ToList().AsReadOnly());
    }

    [Fact]
    public async Task ReturnsChallengesWhereCallerIsActiveParticipant()
    {
        var challenge = BuildChallenge(ChallengeType.CoopGoal, target: 5, PeriodEnd);
        StubChallenges(challenge);
        StubLogs(HabitLog.Create(_callerHabit, Today, 1), HabitLog.Create(_memberHabit, Today, 1));

        var result = await _handler.Handle(new GetUserChallengesQuery(_callerId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        var item = result.Value[0];
        item.CurrentProgress.Should().Be(2);
        item.ParticipantCount.Should().Be(2);
        item.HasLinkedHabits.Should().BeTrue();
    }

    [Fact]
    public async Task ExcludesChallengesTheCallerLeft()
    {
        var joined = BuildChallenge(ChallengeType.CoopGoal, target: 5, PeriodEnd);
        var left = BuildChallenge(ChallengeType.CoopGoal, target: 5, PeriodEnd);
        left.TryLeave(_callerId);
        StubChallenges(joined, left);
        StubLogs();

        var result = await _handler.Handle(new GetUserChallengesQuery(_callerId), CancellationToken.None);

        result.Value.Should().ContainSingle(item => item.Id == joined.Id);
    }

    [Fact]
    public async Task StreakTogether_ReportsSharedStreak()
    {
        var challenge = BuildChallenge(ChallengeType.StreakTogether, target: null, periodEnd: null);
        StubChallenges(challenge);
        StubLogs(
            HabitLog.Create(_callerHabit, Today, 1), HabitLog.Create(_memberHabit, Today, 1),
            HabitLog.Create(_callerHabit, Today.AddDays(-1), 1), HabitLog.Create(_memberHabit, Today.AddDays(-1), 1));

        var result = await _handler.Handle(new GetUserChallengesQuery(_callerId), CancellationToken.None);

        result.Value[0].CurrentProgress.Should().Be(2);
    }

    [Fact]
    public async Task HasLinkedHabitsFalse_WhenCallerHasNoLinkedHabits()
    {
        var challenge = BuildChallenge(ChallengeType.CoopGoal, target: 5, PeriodEnd, callerLinksHabit: false);
        StubChallenges(challenge);
        StubLogs();

        var result = await _handler.Handle(new GetUserChallengesQuery(_callerId), CancellationToken.None);

        result.Value[0].HasLinkedHabits.Should().BeFalse();
    }

    [Fact]
    public async Task ProgressReadFilter_CountsEveryParticipantHabitButExcludesUnlinkedHabits()
    {
        StubChallenges(BuildChallenge(ChallengeType.CoopGoal, target: 5, PeriodEnd));

        Expression<Func<HabitLog, bool>>? readFilter = null;
        _habitLogRepository.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                readFilter = call.Arg<Expression<Func<HabitLog, bool>>>();
                return (IReadOnlyList<HabitLog>)new List<HabitLog>();
            });

        await _handler.Handle(new GetUserChallengesQuery(_callerId), CancellationToken.None);

        readFilter.Should().NotBeNull();
        var matches = readFilter!.Compile();
        matches(HabitLog.Create(_callerHabit, Today, 1)).Should().BeTrue("the caller's linked habit contributes to shared progress");
        matches(HabitLog.Create(_memberHabit, Today, 1)).Should().BeTrue("every participant's linked habit contributes to shared progress");
        matches(HabitLog.Create(Guid.NewGuid(), Today, 1)).Should().BeFalse("a habit linked to no participant must be excluded");
    }

    [Fact]
    public async Task OrdersActiveBeforeCompleted()
    {
        var active = BuildChallenge(ChallengeType.StreakTogether, target: null, periodEnd: null);
        var completed = BuildChallenge(ChallengeType.CoopGoal, target: 1, PeriodEnd);
        completed.MarkCompleted();
        StubChallenges(completed, active);
        StubLogs();

        var result = await _handler.Handle(new GetUserChallengesQuery(_callerId), CancellationToken.None);

        result.Value.Should().HaveCount(2);
        result.Value[0].Status.Should().Be(ChallengeStatus.Active);
        result.Value[1].Status.Should().Be(ChallengeStatus.Completed);
    }
}
