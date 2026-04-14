using FluentAssertions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Goals;

public class LinkHabitsToGoalCommandHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly LinkHabitsToGoalCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();

    public LinkHabitsToGoalCommandHandlerTests()
    {
        _handler = new LinkHabitsToGoalCommandHandler(_goalRepo, _habitRepo, _payGate, _unitOfWork);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    [Fact]
    public async Task Handle_ValidLink_LinksHabitsToGoal()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        var habit1 = Habit.Create(new HabitCreateParams(UserId, "Habit 1", FrequencyUnit.Day, 1)).Value;
        var habit2 = Habit.Create(new HabitCreateParams(UserId, "Habit 2", FrequencyUnit.Day, 1)).Value;

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit1, habit2 });

        var command = new LinkHabitsToGoalCommand(UserId, GoalId, new List<Guid> { habit1.Id, habit2.Id });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Habits.Should().HaveCount(2);
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

        var command = new LinkHabitsToGoalCommand(UserId, GoalId, new List<Guid> { Guid.NewGuid() });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.GoalNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.GoalNotFound);
    }

    [Fact]
    public async Task Handle_TooManyHabits_ReturnsFailure()
    {
        var habitIds = Enumerable.Range(0, AppConstants.MaxHabitsPerGoal + 1)
            .Select(_ => Guid.NewGuid()).ToList();

        var command = new LinkHabitsToGoalCommand(UserId, GoalId, habitIds);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain($"{AppConstants.MaxHabitsPerGoal}");
    }

    [Fact]
    public async Task Handle_EmptyHabitList_ClearsExistingLinks()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        var existingHabit = Habit.Create(new HabitCreateParams(UserId, "Existing", FrequencyUnit.Day, 1)).Value;
        goal.AddHabit(existingHabit);

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>());

        var command = new LinkHabitsToGoalCommand(UserId, GoalId, new List<Guid>());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Habits.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ReplacesExistingLinks()
    {
        var goal = Goal.Create(UserId, "Goal", 100, "km").Value;
        var oldHabit = Habit.Create(new HabitCreateParams(UserId, "Old", FrequencyUnit.Day, 1)).Value;
        var newHabit = Habit.Create(new HabitCreateParams(UserId, "New", FrequencyUnit.Day, 1)).Value;
        goal.AddHabit(oldHabit);

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { newHabit });

        var command = new LinkHabitsToGoalCommand(UserId, GoalId, new List<Guid> { newHabit.Id });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Habits.Should().HaveCount(1);
        goal.Habits.Should().Contain(newHabit);
        goal.Habits.Should().NotContain(oldHabit);
    }

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Goals are a Pro feature"));

        var command = new LinkHabitsToGoalCommand(UserId, GoalId, new List<Guid> { Guid.NewGuid() });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
