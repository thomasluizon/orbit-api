using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Goals;

public class UpdateGoalStatusCommandHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IGoalCompletionService _goalCompletionService = Substitute.For<IGoalCompletionService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly UpdateGoalStatusCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public UpdateGoalStatusCommandHandlerTests()
    {
        _handler = new UpdateGoalStatusCommandHandler(
            _goalRepo, _payGate, _goalCompletionService, _unitOfWork, _userDateService, _cache);
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Today);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
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
        await _goalCompletionService.Received(1).SaveCompletedGoalAsync(
            UserId, goal.Id, Arg.Any<CancellationToken>());
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
        await _goalCompletionService.DidNotReceive().SaveCompletedGoalAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Reactivate_SetsStatusToActive()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        goal.MarkCompleted();        SetupGoalFound(goal);

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
        result.Error.Should().Be(ErrorMessages.GoalNotFound.Message);
        result.ErrorCode.Should().Be(ErrorCodes.GoalNotFound);
    }

    [Fact]
    public async Task Handle_CompletionPipelineThrows_Propagates()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        SetupGoalFound(goal);

        _goalCompletionService.SaveCompletedGoalAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("gamification error"));

        var command = new UpdateGoalStatusCommand(UserId, GoalId, GoalStatus.Completed);

        var act = () => _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
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

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Goals are a Pro feature"));

        var command = new UpdateGoalStatusCommand(UserId, GoalId, GoalStatus.Completed);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
