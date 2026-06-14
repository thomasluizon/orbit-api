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
    private readonly IAppConfigService _appConfigService = Substitute.For<IAppConfigService>();
    private readonly MoveHabitParentCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public MoveHabitParentCommandHandlerTests()
    {
        _handler = new MoveHabitParentCommandHandler(_habitRepo, _unitOfWork, _appConfigService);
        _appConfigService.GetAsync("MaxHabitDepth", 5, Arg.Any<CancellationToken>())
            .Returns(5);
    }

    private static Habit CreateTestHabit(string title = "Test Habit")
    {
        return Habit.Create(new HabitCreateParams(
            UserId, title, FrequencyUnit.Day, 1,
            DueDate: Today)).Value;
    }

    [Fact]
    public async Task Handle_ValidCommand_MovesToNewParent()
    {
        var habit = CreateTestHabit("Child");
        var newParent = CreateTestHabit("New Parent");

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            includes: Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit, newParent);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit, newParent }.AsReadOnly());

        var command = new MoveHabitParentCommand(UserId, habit.Id, newParent.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NullParent_PromotesToTopLevel()
    {
        var habit = CreateTestHabit("Child");
        habit.SetParentHabitId(Guid.NewGuid());
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
        var parent = CreateTestHabit("Parent");
        var child = CreateTestHabit("Child");
        child.SetParentHabitId(parent.Id);

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            includes: Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(parent, child);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent, child }.AsReadOnly());

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

    [Fact]
    public async Task Handle_MoveExceedsMaxDepth_ReturnsFailure()
    {
        var chain = BuildParentChain(5);
        var deepestParent = chain[^1];
        var movee = CreateTestHabit("Movee");
        var allHabits = chain.Append(movee).ToList();

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            includes: Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(movee, deepestParent);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(allHabits.AsReadOnly());

        var command = new MoveHabitParentCommand(UserId, movee.Id, deepestParent.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("nesting depth");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MoveWithinMaxDepth_Succeeds()
    {
        var chain = BuildParentChain(5);
        var validParent = chain[3];
        var movee = CreateTestHabit("Movee");
        var allHabits = chain.Append(movee).ToList();

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            includes: Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(movee, validParent);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(allHabits.AsReadOnly());

        var command = new MoveHabitParentCommand(UserId, movee.Id, validParent.Id);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        movee.ParentHabitId.Should().Be(validParent.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static List<Habit> BuildParentChain(int length)
    {
        var chain = new List<Habit>();
        Guid? parentId = null;
        for (var i = 0; i < length; i++)
        {
            var node = CreateTestHabit($"Level {i}");
            if (parentId is not null)
                node.SetParentHabitId(parentId);
            chain.Add(node);
            parentId = node.Id;
        }
        return chain;
    }
}
