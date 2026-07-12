using FluentAssertions;
using NSubstitute;
using Orbit.Application.Goals.Queries;
using Orbit.Application.Goals.Services;
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
    private readonly IStreakGoalReadSyncer _streakGoalReadSyncer = Substitute.For<IStreakGoalReadSyncer>();
    private readonly GetGoalsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public GetGoalsQueryHandlerTests()
    {
        _handler = new GetGoalsQueryHandler(_goalRepo, _payGate, _userDateService, _streakGoalReadSyncer);
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Success());
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _streakGoalReadSyncer.ComputeFreshValuesAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
    }

    private static Goal CreateTestGoal(string title = "Test Goal", decimal target = 100, decimal current = 0)
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(UserId, title, target, "units", Deadline: Today.AddDays(30))).Value;
        if (current > 0) goal.UpdateProgress(current);
        return goal;
    }

    private void ArrangePage(IReadOnlyList<Goal> pageItems, int totalCount)
    {
        _goalRepo.FindPagedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IOrderedQueryable<Goal>>>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((pageItems, totalCount));
    }

    [Fact]
    public async Task Handle_MapsRepositoryPageAndReportsTotalCount()
    {
        ArrangePage(new List<Goal> { CreateTestGoal("Goal 1"), CreateTestGoal("Goal 2") }, 2);

        var result = await _handler.Handle(new GetGoalsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Handle_NoGoals_ReturnsEmptyResult()
    {
        ArrangePage(new List<Goal>(), 0);

        var result = await _handler.Handle(new GetGoalsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.TotalPages.Should().Be(0);
    }

    [Fact]
    public async Task Handle_ForwardsPagingAndComputesTotalPagesFromRepositoryCount()
    {
        var pageItems = new List<Goal> { CreateTestGoal("Goal 1"), CreateTestGoal("Goal 2") };
        ArrangePage(pageItems, 5);

        var result = await _handler.Handle(new GetGoalsQuery(UserId, Page: 1, PageSize: 2), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(2);
        result.Value.TotalCount.Should().Be(5);
        result.Value.TotalPages.Should().Be(3);
        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(2);
        await _goalRepo.Received(1).FindPagedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IOrderedQueryable<Goal>>>(),
            1,
            2,
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StatusFilter_BuildsPredicateMatchingOnlyThatStatusForTheUser()
    {
        var predicate = await CapturePredicate(new GetGoalsQuery(UserId, StatusFilter: GoalStatus.Active));

        var active = CreateTestGoal("Active");
        var completed = CreateTestGoal("Completed", target: 10, current: 10);
        var foreignActive = Goal.Create(new Goal.CreateGoalParams(Guid.NewGuid(), "Foreign", 100, "units")).Value;

        completed.Status.Should().Be(GoalStatus.Completed);
        predicate(active).Should().BeTrue();
        predicate(completed).Should().BeFalse();
        predicate(foreignActive).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_NoStatusFilter_BuildsPredicateMatchingEveryStatusForTheUser()
    {
        var predicate = await CapturePredicate(new GetGoalsQuery(UserId));

        var active = CreateTestGoal("Active");
        var completed = CreateTestGoal("Completed", target: 10, current: 10);
        var foreignActive = Goal.Create(new Goal.CreateGoalParams(Guid.NewGuid(), "Foreign", 100, "units")).Value;

        predicate(active).Should().BeTrue();
        predicate(completed).Should().BeTrue();
        predicate(foreignActive).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ProgressPercentage_CalculatedCorrectly()
    {
        ArrangePage(new List<Goal> { CreateTestGoal("Progress Goal", target: 200, current: 50) }, 1);

        var result = await _handler.Handle(new GetGoalsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].ProgressPercentage.Should().Be(25);
    }

    [Fact]
    public async Task Handle_TrackingStatus_OnTrackWhenDeadlineFar()
    {
        ArrangePage(new List<Goal> { CreateTestGoal("Far Deadline", target: 100, current: 50) }, 1);

        var result = await _handler.Handle(new GetGoalsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].TrackingStatus.Should().Be("on_track");
    }

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.PayGateFailure("Goals are a Pro feature"));

        var result = await _handler.Handle(new GetGoalsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Orbit.Domain.Common.Result.PayGateErrorCode);
        await _goalRepo.DidNotReceive().FindPagedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IOrderedQueryable<Goal>>>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ComputesFreshStreakValuesBeforeReadingThem()
    {
        var computed = false;
        _streakGoalReadSyncer
            .ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>())
            .Returns(_ => { computed = true; return new Dictionary<Guid, int>(); });

        _goalRepo.FindPagedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IOrderedQueryable<Goal>>>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                computed.Should().BeTrue();
                return ((IReadOnlyList<Goal>)new List<Goal> { CreateTestGoal() }, 1);
            });

        var result = await _handler.Handle(new GetGoalsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _streakGoalReadSyncer.Received(1).ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ProjectsFreshStreakValueOverPersistedCurrentValue()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", 7, "days", Type: GoalType.Streak)).Value;

        _streakGoalReadSyncer
            .ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [goal.Id] = 5 });
        ArrangePage(new List<Goal> { goal }, 1);

        var result = await _handler.Handle(new GetGoalsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items[0].CurrentValue.Should().Be(5);
    }

    private async Task<Func<Goal, bool>> CapturePredicate(GetGoalsQuery query)
    {
        Expression<Func<Goal, bool>>? captured = null;
        _goalRepo.FindPagedAsync(
            Arg.Do<Expression<Func<Goal, bool>>>(p => captured = p),
            Arg.Any<Func<IQueryable<Goal>, IOrderedQueryable<Goal>>>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(((IReadOnlyList<Goal>)new List<Goal>(), 0));

        await _handler.Handle(query, CancellationToken.None);
        captured.Should().NotBeNull();
        return captured!.Compile();
    }
}
