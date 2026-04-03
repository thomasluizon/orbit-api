using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class GoalReviewToolTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GoalReviewTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GoalReviewToolTests()
    {
        _tool = new GoalReviewTool(_goalRepo, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
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
