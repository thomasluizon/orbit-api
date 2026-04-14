using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Goals;

public class UpdateGoalCommandHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UpdateGoalCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();

    public UpdateGoalCommandHandlerTests()
    {
        _handler = new UpdateGoalCommandHandler(_goalRepo, _payGate, _unitOfWork);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    [Fact]
    public async Task Handle_ValidUpdate_UpdatesGoalAndSaves()
    {
        var goal = Goal.Create(UserId, "Old title", 100, "km").Value;
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var command = new UpdateGoalCommand(UserId, GoalId, "New title", "Updated desc", 200, "miles", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Title.Should().Be("New title");
        goal.Description.Should().Be("Updated desc");
        goal.TargetValue.Should().Be(200);
        goal.Unit.Should().Be("miles");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GoalNotFound_ReturnsFailure()
    {
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Goal?)null);

        var command = new UpdateGoalCommand(UserId, GoalId, "Title", null, 10, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.GoalNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.GoalNotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyTitle_ReturnsFailure()
    {
        var goal = Goal.Create(UserId, "Old title", 100, "km").Value;
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var command = new UpdateGoalCommand(UserId, GoalId, "", null, 10, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title");
    }

    [Fact]
    public async Task Handle_ZeroTargetValue_ReturnsFailure()
    {
        var goal = Goal.Create(UserId, "Title", 100, "km").Value;
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var command = new UpdateGoalCommand(UserId, GoalId, "Title", null, 0, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Target value");
    }

    [Fact]
    public async Task Handle_WithDeadline_UpdatesDeadline()
    {
        var goal = Goal.Create(UserId, "Title", 100, "km").Value;
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var deadline = new DateOnly(2027, 6, 15);
        var command = new UpdateGoalCommand(UserId, GoalId, "Title", null, 100, "km", deadline);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Deadline.Should().Be(deadline);
    }

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Goals are a Pro feature"));

        var command = new UpdateGoalCommand(UserId, GoalId, "Title", null, 100, "km", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
