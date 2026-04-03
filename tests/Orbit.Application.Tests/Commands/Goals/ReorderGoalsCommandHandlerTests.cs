using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Goals;

public class ReorderGoalsCommandHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ReorderGoalsCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public ReorderGoalsCommandHandlerTests()
    {
        _handler = new ReorderGoalsCommandHandler(_goalRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_ValidReorder_UpdatesPositionsAndSaves()
    {
        var goal1 = Goal.Create(UserId, "Goal 1", 10, "units", position: 0).Value;
        var goal2 = Goal.Create(UserId, "Goal 2", 20, "units", position: 1).Value;

        var callCount = 0;
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                return callCount == 1 ? goal1 : goal2;
            });

        var positions = new List<GoalPositionUpdate>
        {
            new(goal1.Id, 1),
            new(goal2.Id, 0)
        };
        var command = new ReorderGoalsCommand(UserId, positions);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal1.Position.Should().Be(1);
        goal2.Position.Should().Be(0);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyList_SavesWithoutChanges()
    {
        var command = new ReorderGoalsCommand(UserId, new List<GoalPositionUpdate>());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
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

        var positions = new List<GoalPositionUpdate>
        {
            new(Guid.NewGuid(), 0)
        };
        var command = new ReorderGoalsCommand(UserId, positions);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.GoalNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.GoalNotFound);
    }
}
