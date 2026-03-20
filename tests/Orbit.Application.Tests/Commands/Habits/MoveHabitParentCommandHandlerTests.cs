using FluentAssertions;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

public class MoveHabitParentCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MoveHabitParentCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public MoveHabitParentCommandHandlerTests()
    {
        _handler = new MoveHabitParentCommandHandler(_habitRepo, _unitOfWork);
    }

    private static Habit CreateTestHabit(string title = "Test Habit")
    {
        return Habit.Create(
            UserId, title, FrequencyUnit.Day, 1,
            dueDate: Today).Value;
    }

    [Fact]
    public async Task Handle_ValidCommand_MovesToNewParent()
    {
        var habit = CreateTestHabit("Child");
        var newParent = CreateTestHabit("New Parent");

        // First call returns the habit, second returns the new parent, third for cycle check
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            includes: Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit, newParent, newParent);

        var command = new MoveHabitParentCommand(UserId, habit.Id, newParent.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NullParent_PromotesToTopLevel()
    {
        var habit = CreateTestHabit("Child");
        habit.SetParentHabitId(Guid.NewGuid()); // Start with a parent

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            includes: Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new MoveHabitParentCommand(UserId, habit.Id, null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.ParentHabitId.Should().BeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CircularReference_ReturnsFailure()
    {
        // Parent -> Child. Try to move Parent under Child => circular.
        var parent = CreateTestHabit("Parent");
        var child = CreateTestHabit("Child");
        child.SetParentHabitId(parent.Id);

        var callCount = 0;
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            includes: Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                // 1st call: find the habit being moved (parent)
                if (callCount == 1) return parent;
                // 2nd call: find the target parent (child)
                if (callCount == 2) return child;
                // 3rd call: WouldCreateCycle walks up from child -> finds child.ParentHabitId == parent.Id == habitId
                if (callCount == 3) return child;
                return null;
            });

        var command = new MoveHabitParentCommand(UserId, parent.Id, child.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("descendant");
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            includes: Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var command = new MoveHabitParentCommand(UserId, Guid.NewGuid(), Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Habit not found.");
    }
}
