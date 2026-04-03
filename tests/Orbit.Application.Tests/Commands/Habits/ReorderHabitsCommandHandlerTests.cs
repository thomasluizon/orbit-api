using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

public class ReorderHabitsCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ReorderHabitsCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public ReorderHabitsCommandHandlerTests()
    {
        _handler = new ReorderHabitsCommandHandler(_habitRepo, _unitOfWork);
    }

    [Fact]
    public async Task Handle_ValidReorder_UpdatesPositionsAndSaves()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1)).Value;
        var habit2 = Habit.Create(new HabitCreateParams(UserId, "Habit 2", FrequencyUnit.Day, 1)).Value;
        habit1.SetPosition(0);
        habit2.SetPosition(1);

        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit1, habit2 });

        var positions = new List<HabitPositionUpdate>
        {
            new(habit1.Id, 1),
            new(habit2.Id, 0)
        };
        var command = new ReorderHabitsCommand(UserId, positions);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit1.Position.Should().Be(1);
        habit2.Position.Should().Be(0);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyList_SucceedsWithoutChanges()
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>());

        var command = new ReorderHabitsCommand(UserId, new List<HabitPositionUpdate>());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>());

        var positions = new List<HabitPositionUpdate>
        {
            new(Guid.NewGuid(), 0)
        };
        var command = new ReorderHabitsCommand(UserId, positions);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.HabitNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public async Task Handle_MultipleHabits_LoadsInSingleQuery()
    {
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "H1", FrequencyUnit.Day, 1)).Value;
        var habit2 = Habit.Create(new HabitCreateParams(UserId, "H2", FrequencyUnit.Day, 1)).Value;
        var habit3 = Habit.Create(new HabitCreateParams(UserId, "H3", FrequencyUnit.Day, 1)).Value;

        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit1, habit2, habit3 });

        var positions = new List<HabitPositionUpdate>
        {
            new(habit1.Id, 2),
            new(habit2.Id, 0),
            new(habit3.Id, 1)
        };
        var command = new ReorderHabitsCommand(UserId, positions);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Only one call to FindTrackedAsync (batch load)
        await _habitRepo.Received(1).FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>());
    }
}
