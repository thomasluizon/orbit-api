using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Goals;

public class GetGoalByIdQueryHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly GetGoalByIdQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();

    public GetGoalByIdQueryHandlerTests()
    {
        _handler = new GetGoalByIdQueryHandler(_goalRepo, _payGate);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success());
    }

    private static Goal CreateTestGoal(string title = "Test Goal", decimal target = 100)
    {
        return Goal.Create(new Goal.CreateGoalParams(UserId, title, target, "units", "A test goal")).Value;
    }

    [Fact]
    public async Task Handle_GoalFound_ReturnsSuccess()
    {
        var goal = CreateTestGoal("My Goal");

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("My Goal");
        result.Value.Description.Should().Be("A test goal");
        result.Value.TargetValue.Should().Be(100);
        result.Value.CurrentValue.Should().Be(0);
        result.Value.Unit.Should().Be("units");
    }

    [Fact]
    public async Task Handle_GoalNotFound_ReturnsFailure()
    {
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Goal?)null);

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Goal not found");
        result.ErrorCode.Should().Be("GOAL_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsGoalNotFound()
    {
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Goal?)null);

        var wrongUserId = Guid.NewGuid();
        var query = new GetGoalByIdQuery(wrongUserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("GOAL_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_ProgressPercentage_CalculatedCorrectly()
    {
        var goal = CreateTestGoal("Progress", target: 200);
        goal.UpdateProgress(100);

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProgressPercentage.Should().Be(50);
    }

    [Fact]
    public async Task Handle_ProgressPercentage_CappedAt100()
    {
        var goal = CreateTestGoal("Overcomplete", target: 50);
        goal.UpdateProgress(100);

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public async Task Handle_GoalFound_MapsLinkedHabits()
    {
        var goal = CreateTestGoal("With Habits");

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LinkedHabits.Should().BeEmpty();
        result.Value.ProgressHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.PayGateFailure("Goals are a Pro feature"));

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Domain.Common.Result.PayGateErrorCode);
    }
}
