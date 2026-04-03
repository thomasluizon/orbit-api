using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Goals;

public class UpdateGoalStatusCommandHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UpdateGoalStatusCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();

    public UpdateGoalStatusCommandHandlerTests()
    {
        _handler = new UpdateGoalStatusCommandHandler(
            _goalRepo, _gamificationService, _unitOfWork,
            Substitute.For<ILogger<UpdateGoalStatusCommandHandler>>());
    }

    [Fact]
    public async Task Handle_MarkCompleted_SetsStatusAndCallsGamification()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        SetupGoalFound(goal);

        var command = new UpdateGoalStatusCommand(UserId, GoalId, GoalStatus.Completed);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Completed);
        goal.CompletedAtUtc.Should().NotBeNull();
        await _gamificationService.Received(1).ProcessGoalCompleted(UserId, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MarkAbandoned_SetsStatus()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        SetupGoalFound(goal);

        var command = new UpdateGoalStatusCommand(UserId, GoalId, GoalStatus.Abandoned);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Abandoned);
        goal.CompletedAtUtc.Should().BeNull();
        await _gamificationService.DidNotReceive().ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Reactivate_SetsStatusToActive()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        goal.MarkCompleted(); // First complete it
        SetupGoalFound(goal);

        var command = new UpdateGoalStatusCommand(UserId, GoalId, GoalStatus.Active);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Active);
        goal.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Handle_AlreadyCompleted_ReturnsFailure()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        goal.MarkCompleted();
        SetupGoalFound(goal);

        var command = new UpdateGoalStatusCommand(UserId, GoalId, GoalStatus.Completed);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already completed");
    }

    [Fact]
    public async Task Handle_AlreadyAbandoned_ReturnsFailure()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        goal.MarkAbandoned();
        SetupGoalFound(goal);

        var command = new UpdateGoalStatusCommand(UserId, GoalId, GoalStatus.Abandoned);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already abandoned");
    }

    [Fact]
    public async Task Handle_AlreadyActive_ReturnsFailure()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        // Goal starts as Active
        SetupGoalFound(goal);

        var command = new UpdateGoalStatusCommand(UserId, GoalId, GoalStatus.Active);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already active");
    }

    [Fact]
    public async Task Handle_GoalNotFound_ReturnsFailure()
    {
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Goal?)null);

        var command = new UpdateGoalStatusCommand(UserId, GoalId, GoalStatus.Completed);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.GoalNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.GoalNotFound);
    }

    [Fact]
    public async Task Handle_GamificationThrows_StillReturnsSuccess()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        SetupGoalFound(goal);

        _gamificationService.ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("gamification error"));

        var command = new UpdateGoalStatusCommand(UserId, GoalId, GoalStatus.Completed);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_InvalidStatus_ReturnsFailure()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        SetupGoalFound(goal);

        var command = new UpdateGoalStatusCommand(UserId, GoalId, (GoalStatus)99);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid status");
    }

    private void SetupGoalFound(Goal goal)
    {
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);
    }
}
