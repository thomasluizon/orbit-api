using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class GoalReviewToolTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IGoalProgressReadSyncer _goalProgressReadSyncer = Substitute.For<IGoalProgressReadSyncer>();
    private readonly GoalReviewTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GoalReviewToolTests()
    {
        _tool = new GoalReviewTool(_goalRepo, _payGate, _userDateService, _goalProgressReadSyncer);
        _payGate.CanUseGoalReview(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _goalProgressReadSyncer.ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
    }

    [Fact]
    public void IsReadOnly_ReturnsTrue() => _tool.IsReadOnly.Should().BeTrue();

    [Fact]
    public void Name_ReturnsReviewGoals() => _tool.Name.Should().Be("review_goals");

    [Fact]
    public async Task SuccessfulReview_ReturnsGoalSummary()
    {
        var goal = Goal.Create(UserId, "Read books", 12, "books").Value;
        SetupGoals(goal);

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Read books");
        result.EntityName.Should().Contain("12");
    }

    [Fact]
    public async Task NoGoals_ReturnsNoGoalsMessage()
    {
        SetupGoals();

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("No active goals");
    }

    [Fact]
    public async Task FreeUser_ReturnsPayGateFailureBeforeLoadingGoals()
    {
        _payGate.CanUseGoalReview(UserId, Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Goal reviews are a Pro feature"));

        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _goalRepo.DidNotReceive().FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MultipleGoals_ReturnsAllGoals()
    {
        var goal1 = Goal.Create(UserId, "Read books", 12, "books").Value;
        var goal2 = Goal.Create(UserId, "Save money", 5000, "dollars").Value;
        SetupGoals(goal1, goal2);

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Read books");
        result.EntityName.Should().Contain("Save money");
    }

    [Fact]
    public async Task LinkedStandardGoal_UsesFreshDerivedProgress()
    {
        var goal = Goal.Create(UserId, "Read books", 12, "books").Value;
        var habit = Habit.Create(new HabitCreateParams(
            UserId,
            "Read",
            FrequencyUnit.Day,
            1,
            DueDate: Today)).Value;
        goal.AddHabit(habit);
        SetupGoals(goal);
        _goalProgressReadSyncer.ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [goal.Id] = 2 });

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("2/12 books");
        goal.Status.Should().Be(GoalStatus.Active);
    }

    private void SetupGoals(params Goal[] goals)
    {
        _goalRepo.FindAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(goals.ToList().AsReadOnly());
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
