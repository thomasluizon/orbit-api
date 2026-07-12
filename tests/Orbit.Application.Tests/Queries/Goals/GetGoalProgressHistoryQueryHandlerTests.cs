using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Goals;

public class GetGoalProgressHistoryQueryHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<GoalProgressLog> _progressLogRepo = Substitute.For<IGenericRepository<GoalProgressLog>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly GetGoalProgressHistoryQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public GetGoalProgressHistoryQueryHandlerTests()
    {
        _handler = new GetGoalProgressHistoryQueryHandler(_goalRepo, _progressLogRepo, _payGate);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _goalRepo.AnyAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private static GoalProgressLog Log(decimal value, decimal previous, DateTime createdAtUtc, string? note = null, Guid? goalId = null)
    {
        var log = GoalProgressLog.Create(goalId ?? GoalId, previous, value, note);
        typeof(GoalProgressLog).GetProperty(nameof(GoalProgressLog.CreatedAtUtc))!
            .SetValue(log, createdAtUtc);
        return log;
    }

    private void ArrangeLogs(params GoalProgressLog[] logs)
    {
        _progressLogRepo.FindAsync(
            Arg.Any<Expression<Func<GoalProgressLog, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(logs.ToList().AsReadOnly());
    }

    [Fact]
    public async Task Handle_ProjectsRepositoryLogsOrderedAscendingByDate()
    {
        var early = Today.AddDays(-2).ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
        var late = Today.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc);
        ArrangeLogs(
            Log(60, 40, late, "done"),
            Log(40, 20, early));

        var query = new GetGoalProgressHistoryQuery(UserId, GoalId, Today.AddDays(-5), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GoalId.Should().Be(GoalId);
        result.Value.Points.Should().HaveCount(2);
        result.Value.Points[0].Date.Should().Be(Today.AddDays(-2));
        result.Value.Points[0].Value.Should().Be(40);
        result.Value.Points[0].PreviousValue.Should().Be(20);
        result.Value.Points[1].Date.Should().Be(Today);
        result.Value.Points[1].Value.Should().Be(60);
        result.Value.Points[1].Note.Should().Be("done");
    }

    [Fact]
    public async Task Handle_PushesDateRangeToRepositoryPredicate_UtcMidnightInclusiveToExclusive()
    {
        Expression<Func<GoalProgressLog, bool>>? captured = null;
        _progressLogRepo.FindAsync(
            Arg.Do<Expression<Func<GoalProgressLog, bool>>>(p => captured = p),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoalProgressLog>().AsReadOnly());

        var from = Today.AddDays(-3);
        var to = Today;
        await _handler.Handle(new GetGoalProgressHistoryQuery(UserId, GoalId, from, to), CancellationToken.None);

        captured.Should().NotBeNull();
        var matches = captured!.Compile();

        var fromMidnight = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toEndOfDay = to.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc);
        var toNextMidnight = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        matches(Log(1, 0, fromMidnight)).Should().BeTrue();
        matches(Log(1, 0, toEndOfDay)).Should().BeTrue();
        matches(Log(1, 0, fromMidnight.AddTicks(-1))).Should().BeFalse();
        matches(Log(1, 0, toNextMidnight)).Should().BeFalse();
        matches(Log(1, 0, fromMidnight, goalId: Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_GoalNotFound_ReturnsFailureWithoutQueryingLogs()
    {
        _goalRepo.AnyAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var query = new GetGoalProgressHistoryQuery(UserId, GoalId, Today.AddDays(-5), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Goal not found");
        await _progressLogRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<GoalProgressLog, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Goals are a Pro feature"));

        var query = new GetGoalProgressHistoryQuery(UserId, GoalId, Today.AddDays(-5), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _goalRepo.DidNotReceive().AnyAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>());
    }
}
