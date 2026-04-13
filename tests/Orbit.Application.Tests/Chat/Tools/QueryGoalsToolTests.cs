using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class QueryGoalsToolTests
{
    private readonly IGenericRepository<Goal> _goalRepository = Substitute.For<IGenericRepository<Goal>>();
    private readonly QueryGoalsTool _tool;
    private static readonly Guid UserId = Guid.NewGuid();

    public QueryGoalsToolTests()
    {
        _tool = new QueryGoalsTool(_goalRepository);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFormattedGoals()
    {
        var activeGoal = Goal.Create(new Goal.CreateGoalParams(
            UserId,
            "Run a marathon",
            42,
            "km",
            "Train for race day",
            Type: GoalType.Standard)).Value;

        _goalRepository.FindAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Goal>>([activeGoal]));

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Run a marathon");
        result.EntityName.Should().Contain("Train for race day");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNoGoalsMessage_WhenSearchMisses()
    {
        var activeGoal = Goal.Create(new Goal.CreateGoalParams(
            UserId,
            "Run a marathon",
            42,
            "km")).Value;

        _goalRepository.FindAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Goal>>([activeGoal]));

        var result = await Execute("""{"search":"books"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("No goals found matching the given filters.");
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
