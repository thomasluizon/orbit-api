using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Goals;

public class GetGoalByIdQueryHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetGoalByIdQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public GetGoalByIdQueryHandlerTests()
    {
        _handler = new GetGoalByIdQueryHandler(_goalRepo, _payGate, _userDateService);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success());
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static Goal CreateTestGoal(string title = "Test Goal", decimal target = 100)
    {
        return Goal.Create(new Goal.CreateGoalParams(UserId, title, target, "units", "A test goal")).Value;
    }

    private void ArrangeGoal(Goal? goal)
    {
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((goal is null ? new List<Goal>() : [goal]).AsReadOnly());
    }

    private static void SetCreatedAtUtc(Habit habit, DateOnly localDate)
    {
        typeof(Habit)
            .GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Handle_GoalFound_ReturnsSuccess()
    {
        var goal = CreateTestGoal("My Goal");
        ArrangeGoal(goal);

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("My Goal");
        result.Value.Description.Should().Be("A test goal");
        result.Value.TargetValue.Should().Be(100);
        result.Value.CurrentValue.Should().Be(0);
        result.Value.Unit.Should().Be("units");
    }

    [Fact]
    public async Task Handle_GoalNotFound_ReturnsFailure()
    {
        ArrangeGoal(null);

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Goal not found");
        result.ErrorCode.Should().Be("GOAL_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_WrongUser_ReturnsGoalNotFound()
    {
        ArrangeGoal(null);

        var wrongUserId = Guid.NewGuid();
        var query = new GetGoalByIdQuery(wrongUserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("GOAL_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_ProgressPercentage_CalculatedCorrectly()
    {
        var goal = CreateTestGoal("Progress", target: 200);
        goal.UpdateProgress(100);
        ArrangeGoal(goal);

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProgressPercentage.Should().Be(50);
    }

    [Fact]
    public async Task Handle_ProgressPercentage_CappedAt100()
    {
        var goal = CreateTestGoal("Overcomplete", target: 50);
        goal.UpdateProgress(100);
        ArrangeGoal(goal);

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProgressPercentage.Should().Be(100);
    }

    [Fact]
    public async Task Handle_GoalFound_MapsLinkedHabits()
    {
        var goal = CreateTestGoal("With Habits");
        ArrangeGoal(goal);

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LinkedHabits.Should().BeEmpty();
        result.Value.ProgressHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.PayGateFailure("Goals are a Pro feature"));

        var query = new GetGoalByIdQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Domain.Common.Result.PayGateErrorCode);
    }

    [Fact]
    public async Task Handle_WithBadHabitLinkedStreakGoal_ReturnsFreshCurrentValueWithoutPersistingCompletion()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", 7, "days", Type: GoalType.Streak)).Value;

        var badHabit = Habit.Create(new HabitCreateParams(
            UserId, "Doom scrolling", FrequencyUnit.Day, 1, IsBadHabit: true, DueDate: Today)).Value;

        SetCreatedAtUtc(badHabit, Today.AddDays(-1));
        badHabit.AddGoal(goal);
        goal.AddHabit(badHabit);
        ArrangeGoal(goal);

        var result = await _handler.Handle(new GetGoalByIdQuery(UserId, GoalId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentValue.Should().Be(2);
        goal.Status.Should().Be(GoalStatus.Active);
    }
}
