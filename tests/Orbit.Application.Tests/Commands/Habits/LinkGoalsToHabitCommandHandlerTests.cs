using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Habits;

public class LinkGoalsToHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly LinkGoalsToHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public LinkGoalsToHabitCommandHandlerTests()
    {
        _handler = new LinkGoalsToHabitCommandHandler(_habitRepo, _goalRepo, _unitOfWork);
    }

    private static Habit CreateTestHabit() =>
        Habit.Create(new HabitCreateParams(UserId, "Test Habit", FrequencyUnit.Day, 1, DueDate: Today)).Value;

    private static Goal CreateTestGoal(string title = "Be Healthy") =>
        Goal.Create(UserId, title, 10, "kg").Value;

    [Fact]
    public async Task Handle_ValidCommand_LinksGoalsToHabit()
    {
        var habit = CreateTestHabit();
        var goal1 = CreateTestGoal("Goal 1");
        var goal2 = CreateTestGoal("Goal 2");

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()).Returns(habit);

        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>()).Returns(new List<Goal> { goal1, goal2 });

        var command = new LinkGoalsToHabitCommand(UserId, habit.Id, [goal1.Id, goal2.Id]);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()).Returns((Habit?)null);

        var command = new LinkGoalsToHabitCommand(UserId, Guid.NewGuid(), [Guid.NewGuid()]);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.HabitNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.HabitNotFound);
    }

    [Fact]
    public async Task Handle_NoMatchingGoals_ClearsExistingGoals()
    {
        var habit = CreateTestHabit();
        var existingGoal = CreateTestGoal("Existing");
        habit.AddGoal(existingGoal);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()).Returns(habit);

        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>()).Returns(new List<Goal>());

        var command = new LinkGoalsToHabitCommand(UserId, habit.Id, [Guid.NewGuid()]);
        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        habit.Goals.Should().BeEmpty();
    }
}
