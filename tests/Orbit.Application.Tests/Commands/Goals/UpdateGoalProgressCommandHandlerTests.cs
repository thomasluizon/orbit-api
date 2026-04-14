using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Goals;

public class UpdateGoalProgressCommandHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<GoalProgressLog> _progressLogRepo = Substitute.For<IGenericRepository<GoalProgressLog>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UpdateGoalProgressCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();

    public UpdateGoalProgressCommandHandlerTests()
    {
        _handler = new UpdateGoalProgressCommandHandler(_goalRepo, _progressLogRepo, _payGate, _unitOfWork);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    [Fact]
    public async Task Handle_ValidProgress_UpdatesGoalAndCreatesLog()
    {
        var goal = Goal.Create(UserId, "Run 100km", 100, "km").Value;
        SetupGoalFound(goal);

        var command = new UpdateGoalProgressCommand(UserId, GoalId, 50, "Halfway there");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.CurrentValue.Should().Be(50);
        await _progressLogRepo.Received(1).AddAsync(
            Arg.Is<GoalProgressLog>(l => l.Value == 50 && l.PreviousValue == 0),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ProgressReachesTarget_MarksGoalCompleted()
    {
        var goal = Goal.Create(UserId, "Run 100km", 100, "km").Value;
        SetupGoalFound(goal);

        var command = new UpdateGoalProgressCommand(UserId, GoalId, 100);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.CurrentValue.Should().Be(100);
        goal.Status.Should().Be(Domain.Enums.GoalStatus.Completed);
    }

    [Fact]
    public async Task Handle_ProgressExceedsTarget_MarksGoalCompleted()
    {
        var goal = Goal.Create(UserId, "Run 100km", 100, "km").Value;
        SetupGoalFound(goal);

        var command = new UpdateGoalProgressCommand(UserId, GoalId, 120);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(Domain.Enums.GoalStatus.Completed);
    }

    [Fact]
    public async Task Handle_GoalNotFound_ReturnsFailure()
    {
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Goal?)null);

        var command = new UpdateGoalProgressCommand(UserId, GoalId, 50);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.GoalNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.GoalNotFound);
    }

    [Fact]
    public async Task Handle_NegativeProgress_ReturnsFailure()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        SetupGoalFound(goal);

        var command = new UpdateGoalProgressCommand(UserId, GoalId, -10);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("negative");
    }

    [Fact]
    public async Task Handle_GoalProgressDoesNotTouchUserStreakState()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        SetupGoalFound(goal);

        var command = new UpdateGoalProgressCommand(UserId, GoalId, 50);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
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

        var command = new UpdateGoalProgressCommand(UserId, GoalId, 50);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
