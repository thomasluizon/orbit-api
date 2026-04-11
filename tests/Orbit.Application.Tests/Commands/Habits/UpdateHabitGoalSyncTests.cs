using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

/// <summary>
/// Additional tests for UpdateHabitCommandHandler covering goal sync branches.
/// </summary>
public class UpdateHabitGoalSyncTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<SentReminder> _sentReminderRepo = Substitute.For<IGenericRepository<SentReminder>>();
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly UpdateHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public UpdateHabitGoalSyncTests()
    {
        _handler = new UpdateHabitCommandHandler(
            _habitRepo, _sentReminderRepo, _goalRepo, _userDateService, _unitOfWork, _cache);

        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
    }

    [Fact]
    public async Task Handle_WithGoalIds_SyncsGoalLinks()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        var goal = Goal.Create(UserId, "Fitness", 10, "sessions").Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal });

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Test", null, FrequencyUnit.Day, 1,
            GoalIds: new List<Guid> { goal.Id });

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalRepo.Received(1).FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithEmptyGoalIds_ClearsGoalLinks()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Test", null, FrequencyUnit.Day, 1,
            GoalIds: new List<Guid>());

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WithoutGoalIds_DoesNotQueryGoalRepo()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Test", null, FrequencyUnit.Day, 1);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalRepo.DidNotReceive().FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoDueTimeChange_DoesNotClearReminders()
    {
        var habit = Habit.Create(new HabitCreateParams(
            UserId, "Test", FrequencyUnit.Day, 1, DueDate: Today)).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(habit);

        var command = new UpdateHabitCommand(
            UserId, habit.Id, "Updated", null, FrequencyUnit.Day, 1,
            Options: new UpdateHabitCommandOptions(DueTime: null));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _sentReminderRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<SentReminder, bool>>>(),
            Arg.Any<CancellationToken>());
    }
}
