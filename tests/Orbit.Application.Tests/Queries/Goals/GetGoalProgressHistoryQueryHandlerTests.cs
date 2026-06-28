using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using System.Reflection;

namespace Orbit.Application.Tests.Queries.Goals;

public class GetGoalProgressHistoryQueryHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly GetGoalProgressHistoryQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public GetGoalProgressHistoryQueryHandlerTests()
    {
        _handler = new GetGoalProgressHistoryQueryHandler(_goalRepo, _payGate);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    private static Goal CreateGoalWithLogs(params (decimal Value, decimal Previous, DateOnly Date)[] logs)
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, "Read", 100, "pages")).Value;
        var field = typeof(Goal).GetField("_progressLogs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (List<GoalProgressLog>)field.GetValue(goal)!;
        foreach (var (value, previous, date) in logs)
        {
            var log = GoalProgressLog.Create(goal.Id, previous, value);
            typeof(GoalProgressLog).GetProperty(nameof(GoalProgressLog.CreatedAtUtc))!
                .SetValue(log, date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            list.Add(log);
        }

        return goal;
    }

    private void ArrangeGoal(Goal? goal)
    {
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((goal is null ? new List<Goal>() : [goal]).AsReadOnly());
    }

    [Fact]
    public async Task Handle_ReturnsOnlyInRangePointsOrderedAscending()
    {
        var goal = CreateGoalWithLogs(
            (20, 0, Today.AddDays(-10)),
            (60, 40, Today),
            (40, 20, Today.AddDays(-2)));
        ArrangeGoal(goal);

        var query = new GetGoalProgressHistoryQuery(UserId, GoalId, Today.AddDays(-5), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GoalId.Should().Be(goal.Id);
        result.Value.Points.Should().HaveCount(2);
        result.Value.Points[0].Date.Should().Be(Today.AddDays(-2));
        result.Value.Points[0].Value.Should().Be(40);
        result.Value.Points[0].PreviousValue.Should().Be(20);
        result.Value.Points[1].Date.Should().Be(Today);
        result.Value.Points[1].Value.Should().Be(60);
    }

    [Fact]
    public async Task Handle_GoalNotFound_ReturnsFailure()
    {
        ArrangeGoal(null);

        var query = new GetGoalProgressHistoryQuery(UserId, GoalId, Today.AddDays(-5), Today);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Goal not found");
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
    }
}
