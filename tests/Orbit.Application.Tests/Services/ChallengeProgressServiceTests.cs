using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Challenges.Services;
using Orbit.Application.Gamification.Services;
using Orbit.Application.Tests.Social;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Services;

public class ChallengeProgressServiceTests
{
    private readonly IGenericRepository<Challenge> _challengeRepository = Substitute.For<IGenericRepository<Challenge>>();
    private readonly IGenericRepository<ChallengeParticipant> _participantRepository = Substitute.For<IGenericRepository<ChallengeParticipant>>();
    private readonly IGenericRepository<ChallengeParticipantHabit> _participantHabitRepository = Substitute.For<IGenericRepository<ChallengeParticipantHabit>>();
    private readonly IGenericRepository<HabitLog> _habitLogRepository = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<User> _userRepository = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<UserAchievement> _achievementRepository = Substitute.For<IGenericRepository<UserAchievement>>();
    private readonly IGenericRepository<XpAwardLog> _xpAwardLogRepository = Substitute.For<IGenericRepository<XpAwardLog>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();

    private readonly ChallengeProgressService _service;

    private readonly User _user = SocialTestHelpers.OptedInUser("Logger");
    private readonly Guid _habitId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 15);

    public ChallengeProgressServiceTests()
    {
        var repositories = new ChallengeProgressRepositories(
            _challengeRepository, _participantRepository, _participantHabitRepository,
            _habitLogRepository, _userRepository, _achievementRepository);
        _service = new ChallengeProgressService(
            repositories, new XpAwarder(_xpAwardLogRepository), _unitOfWork, _userDateService);

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        SocialTestHelpers.StubFind(_achievementRepository);
        _userRepository.FindTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<User> { _user }.AsReadOnly());
    }

    private Challenge BuildTrackedChallenge(int target)
    {
        var challenge = Challenge.Create(new CreateChallengeParams(
            _user.Id, ChallengeType.CoopGoal, "Challenge", null, target,
            PeriodStartUtc: Today.AddDays(-10), PeriodEndUtc: Today.AddDays(10), JoinCode: "ABC23456")).Value;
        challenge.AddParticipant(_user.Id, [_habitId]);

        var participant = challenge.Participants.First();
        _participantHabitRepository.FindAsync(Arg.Any<Expression<Func<ChallengeParticipantHabit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(participant.LinkedHabits.ToList().AsReadOnly());
        _participantRepository.FindAsync(Arg.Any<Expression<Func<ChallengeParticipant, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ChallengeParticipant> { participant }.AsReadOnly());
        _challengeRepository.FindTrackedAsync(
                Arg.Any<Expression<Func<Challenge, bool>>>(),
                Arg.Any<Func<IQueryable<Challenge>, IQueryable<Challenge>>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Challenge> { challenge }.AsReadOnly());
        return challenge;
    }

    private void StubLogs(params HabitLog[] logs) =>
        _habitLogRepository.FindAsync(Arg.Any<Expression<Func<HabitLog, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(logs.ToList().AsReadOnly());

    [Fact]
    public async Task LogReachingTarget_MarksChallengeCompletedAndSaves()
    {
        var challenge = BuildTrackedChallenge(target: 2);
        StubLogs(HabitLog.Create(_habitId, Today, 1), HabitLog.Create(_habitId, Today.AddDays(-1), 1));

        await _service.EvaluateOnHabitLoggedAsync(_user.Id, _habitId, CancellationToken.None);

        challenge.Status.Should().Be(ChallengeStatus.Completed);
        challenge.CompletedAtUtc.Should().NotBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogReachingTarget_MissionAccomplishedNoOpsWhileAchievementUndefined()
    {
        BuildTrackedChallenge(target: 1);
        StubLogs(HabitLog.Create(_habitId, Today, 1));

        await _service.EvaluateOnHabitLoggedAsync(_user.Id, _habitId, CancellationToken.None);

        await _achievementRepository.DidNotReceive().AddAsync(Arg.Any<UserAchievement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogBelowTarget_DoesNotCompleteOrSave()
    {
        var challenge = BuildTrackedChallenge(target: 5);
        StubLogs(HabitLog.Create(_habitId, Today, 1));

        await _service.EvaluateOnHabitLoggedAsync(_user.Id, _habitId, CancellationToken.None);

        challenge.Status.Should().Be(ChallengeStatus.Active);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitNotLinkedToAnyChallenge_IsNoOp()
    {
        _participantHabitRepository.FindAsync(Arg.Any<Expression<Func<ChallengeParticipantHabit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ChallengeParticipantHabit>().AsReadOnly());

        await _service.EvaluateOnHabitLoggedAsync(_user.Id, _habitId, CancellationToken.None);

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoggerIsNotAnActiveParticipant_IsNoOp()
    {
        BuildTrackedChallenge(target: 1);
        _participantRepository.FindAsync(Arg.Any<Expression<Func<ChallengeParticipant, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ChallengeParticipant>().AsReadOnly());
        StubLogs(HabitLog.Create(_habitId, Today, 1));

        await _service.EvaluateOnHabitLoggedAsync(_user.Id, _habitId, CancellationToken.None);

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
