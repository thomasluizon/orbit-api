using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Habits;

public class DeleteHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly DeleteHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public DeleteHabitCommandHandlerTests()
    {
        _handler = new DeleteHabitCommandHandler(_habitRepo, _unitOfWork, _cache);
    }

    private static Habit CreateTestHabit(Guid? userId = null)
    {
        return Habit.Create(
            userId ?? UserId, "Test Habit", FrequencyUnit.Day, 1,
            dueDate: Today).Value;
    }

    [Fact]
    public async Task Handle_ValidCommand_RemovesHabit()
    {
        var habit = CreateTestHabit();
        _habitRepo.GetByIdAsync(habit.Id, Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new DeleteHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _habitRepo.Received(1).Remove(habit);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var command = new DeleteHabitCommand(UserId, Guid.NewGuid());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Habit not found.");
        _habitRepo.DidNotReceive().Remove(Arg.Any<Habit>());
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsFailure()
    {
        var otherUserId = Guid.NewGuid();
        var habit = CreateTestHabit(otherUserId);
        _habitRepo.GetByIdAsync(habit.Id, Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new DeleteHabitCommand(UserId, habit.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("You don't have permission to delete this habit.");
        _habitRepo.DidNotReceive().Remove(Arg.Any<Habit>());
    }
}
