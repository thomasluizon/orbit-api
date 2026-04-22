using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Goals;

public class GetGoalDetailQueryHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly GetGoalDetailQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public GetGoalDetailQueryHandlerTests()
    {
        _handler = new GetGoalDetailQueryHandler(_goalRepo, _payGate, _userDateService, _unitOfWork);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success());
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    private static Goal CreateTestGoal()
    {
        return Goal.Create(new Goal.CreateGoalParams(UserId, "Test Goal", 100, "pages", "Read 100 pages")).Value;
    }

    private static void SetCreatedAtUtc(Habit habit, DateOnly localDate)
    {
        typeof(Habit)
            .GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Handle_GoalFound_ReturnsDetailWithMetrics()
    {
        var goal = CreateTestGoal();

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var query = new GetGoalDetailQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Goal.Should().NotBeNull();
        result.Value.Goal.Title.Should().Be("Test Goal");
        result.Value.Goal.TargetValue.Should().Be(100);
        result.Value.Goal.Unit.Should().Be("pages");
        result.Value.Metrics.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_GoalNotFound_ReturnsFailure()
    {
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Goal?)null);

        var query = new GetGoalDetailQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Goal not found");
        result.ErrorCode.Should().Be("GOAL_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_GoalFound_ReturnsProgressPercentage()
    {
        var goal = CreateTestGoal();

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var query = new GetGoalDetailQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Goal.ProgressPercentage.Should().Be(0);
        result.Value.Goal.ProgressHistory.Should().BeEmpty();
        result.Value.Goal.LinkedHabits.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_CallsUserDateService()
    {
        var goal = CreateTestGoal();

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var query = new GetGoalDetailQuery(UserId, GoalId);

        await _handler.Handle(query, CancellationToken.None);

        await _userDateService.Received(1).GetUserTodayAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.PayGateFailure("Goals are a Pro feature"));

        var query = new GetGoalDetailQuery(UserId, GoalId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Domain.Common.Result.PayGateErrorCode);
    }

    [Fact]
    public async Task Handle_WithBadHabitLinkedStreakGoal_ReturnsSyncedCurrentValue()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId,
            "Avoid doom scrolling",
            7,
            "days",
            Type: Orbit.Domain.Enums.GoalType.Streak)).Value;

        var badHabit = Habit.Create(new HabitCreateParams(
            UserId,
            "Doom scrolling",
            Orbit.Domain.Enums.FrequencyUnit.Day,
            1,
            IsBadHabit: true,
            DueDate: Today)).Value;

        SetCreatedAtUtc(badHabit, Today.AddDays(-1));
        badHabit.AddGoal(goal);
        goal.AddHabit(badHabit);

        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var result = await _handler.Handle(new GetGoalDetailQuery(UserId, GoalId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Goal.CurrentValue.Should().Be(2);
        goal.CurrentValue.Should().Be(2);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
