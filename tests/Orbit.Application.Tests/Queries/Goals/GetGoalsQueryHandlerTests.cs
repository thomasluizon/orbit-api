using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Goals;

public class GetGoalsQueryHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly GetGoalsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public GetGoalsQueryHandlerTests()
    {
        _handler = new GetGoalsQueryHandler(_goalRepo, _payGate, _userDateService, _unitOfWork);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success());
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal>().AsReadOnly());
    }

    private static Goal CreateTestGoal(string title = "Test Goal", decimal target = 100, decimal current = 0)
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, title, target, "units", Deadline: Today.AddDays(30))).Value;
        if (current > 0) goal.UpdateProgress(current);
        return goal;
    }

    private static void SetCreatedAtUtc(Habit habit, DateOnly localDate)
    {
        typeof(Habit)
            .GetProperty(nameof(Habit.CreatedAtUtc))!
            .SetValue(habit, localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Handle_ReturnsGoalsForUser()
    {
        var goals = new List<Goal>
        {
            CreateTestGoal("Goal 1"),
            CreateTestGoal("Goal 2")
        };

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goals.AsReadOnly());

        var query = new GetGoalsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Handle_NoGoals_ReturnsEmptyResult()
    {
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal>().AsReadOnly());

        var query = new GetGoalsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Handle_StatusFilter_FiltersCorrectly()
    {
        var activeGoal = CreateTestGoal("Active");
        var completedGoal = CreateTestGoal("Completed", target: 10, current: 10);

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { activeGoal, completedGoal }.AsReadOnly());

        var query = new GetGoalsQuery(UserId, StatusFilter: GoalStatus.Active);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].Status.Should().Be(GoalStatus.Active);
    }

    [Fact]
    public async Task Handle_Pagination_ReturnsCorrectPage()
    {
        var goals = Enumerable.Range(1, 5)
            .Select(i => CreateTestGoal($"Goal {i}"))
            .ToList();

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goals.AsReadOnly());

        var query = new GetGoalsQuery(UserId, Page: 1, PageSize: 2);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.TotalCount.Should().Be(5);
        result.Value.TotalPages.Should().Be(3);
        result.Value.Page.Should().Be(1);
    }

    [Fact]
    public async Task Handle_ProgressPercentage_CalculatedCorrectly()
    {
        var goal = CreateTestGoal("Progress Goal", target: 200, current: 50);

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal }.AsReadOnly());

        var query = new GetGoalsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].ProgressPercentage.Should().Be(25);
    }

    [Fact]
    public async Task Handle_TrackingStatus_OnTrackWhenDeadlineFar()
    {
        var goal = CreateTestGoal("Far Deadline", target: 100, current: 50);

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal }.AsReadOnly());

        var query = new GetGoalsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].TrackingStatus.Should().Be("on_track");
    }

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.PayGateFailure("Goals are a Pro feature"));

        var query = new GetGoalsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Domain.Common.Result.PayGateErrorCode);
    }

    [Fact]
    public async Task Handle_WithBadHabitLinkedStreakGoal_PassivelySyncsQuietDayProgress()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId,
            "Avoid doom scrolling",
            7,
            "days",
            Type: GoalType.Streak)).Value;

        var badHabit = Habit.Create(new HabitCreateParams(
            UserId,
            "Doom scrolling",
            FrequencyUnit.Day,
            1,
            IsBadHabit: true,
            DueDate: Today)).Value;

        SetCreatedAtUtc(badHabit, Today.AddDays(-1));
        badHabit.AddGoal(goal);
        goal.AddHabit(badHabit);

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal }.AsReadOnly());
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Goal> { goal }.AsReadOnly());

        var result = await _handler.Handle(new GetGoalsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Items[0].CurrentValue.Should().Be(2);
        goal.CurrentValue.Should().Be(2);
        goal.StreakSyncedAtUtc.Should().NotBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StatusFilter_ReappliesAfterPassiveSyncCompletesGoal()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId,
            "Avoid doom scrolling",
            2,
            "days",
            Type: GoalType.Streak)).Value;

        var badHabit = Habit.Create(new HabitCreateParams(
            UserId,
            "Doom scrolling",
            FrequencyUnit.Day,
            1,
            IsBadHabit: true,
            DueDate: Today)).Value;

        SetCreatedAtUtc(badHabit, Today.AddDays(-1));
        badHabit.AddGoal(goal);
        goal.AddHabit(badHabit);

        var goals = new List<Goal> { goal }.AsReadOnly();

        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goals);
        _goalRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goals);

        var result = await _handler.Handle(
            new GetGoalsQuery(UserId, StatusFilter: GoalStatus.Active),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        goal.Status.Should().Be(GoalStatus.Completed);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
