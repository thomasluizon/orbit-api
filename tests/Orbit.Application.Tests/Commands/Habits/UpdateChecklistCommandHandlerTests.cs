using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Habits;

public class UpdateChecklistCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UpdateChecklistCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public UpdateChecklistCommandHandlerTests()
    {
        _handler = new UpdateChecklistCommandHandler(_habitRepo, _unitOfWork);
    }

    private static Habit CreateTestHabit() =>
        Habit.Create(new HabitCreateParams(UserId, "Test Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;

    [Fact]
    public async Task Handle_ValidCommand_UpdatesChecklist()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()).Returns(habit);

        var items = new List<ChecklistItem>
        {
            new("Drink water", false),
            new("Stretch", true)
        };

        var command = new UpdateChecklistCommand(UserId, habit.Id, items);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.ChecklistItems.Should().HaveCount(2);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()).Returns((Habit?)null);

        var command = new UpdateChecklistCommand(UserId, Guid.NewGuid(), []);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.HabitNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public async Task Handle_EmptyChecklist_ClearsItems()
    {
        var habit = CreateTestHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()).Returns(habit);

        var command = new UpdateChecklistCommand(UserId, habit.Id, []);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.ChecklistItems.Should().BeEmpty();
    }
}
