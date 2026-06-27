using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Goals;

public class RestoreGoalCommandHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly RestoreGoalCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();

    public RestoreGoalCommandHandlerTests()
    {
        _handler = new RestoreGoalCommandHandler(_goalRepo, _payGate, _unitOfWork);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    private void SetupGoals(params Goal[] goals)
    {
        _goalRepo.FindTrackedIgnoringFiltersAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(goals.ToList());
    }

    [Fact]
    public async Task Handle_RestoresGoalAndSaves()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        goal.SoftDelete();
        SetupGoals(goal);

        var result = await _handler.Handle(new RestoreGoalCommand(UserId, GoalId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.IsDeleted.Should().BeFalse();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GoalNotDeleted_ReturnsFailure()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        SetupGoals(goal);

        var result = await _handler.Handle(new RestoreGoalCommand(UserId, GoalId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.GoalNotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GoalNotFound_ReturnsFailure()
    {
        SetupGoals();

        var result = await _handler.Handle(new RestoreGoalCommand(UserId, GoalId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.GoalNotFound);
    }

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Goals are a Pro feature"));

        var result = await _handler.Handle(new RestoreGoalCommand(UserId, GoalId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
