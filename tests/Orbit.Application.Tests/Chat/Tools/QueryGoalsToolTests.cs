using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class QueryGoalsToolTests
{
    private readonly IGenericRepository<Goal> _goalRepository = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IStreakGoalReadSyncer _streakGoalReadSyncer = Substitute.For<IStreakGoalReadSyncer>();
    private readonly QueryGoalsTool _tool;
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public QueryGoalsToolTests()
    {
        _tool = new QueryGoalsTool(_goalRepository, _userDateService, _streakGoalReadSyncer);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _streakGoalReadSyncer.ComputeFreshValuesAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
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

    [Fact]
    public async Task ExecuteAsync_ReflectsFreshStreakValue_FromSyncerComputedBeforeRead()
    {
        var streakGoal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", 7, "days", Type: GoalType.Streak)).Value;

        _streakGoalReadSyncer
            .ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [streakGoal.Id] = 4 });

        _goalRepository.FindAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Goal>>([streakGoal]));

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        await _streakGoalReadSyncer.Received(1).ComputeFreshValuesAsync(UserId, Today, Arg.Any<CancellationToken>());
        result.EntityName.Should().Contain("4/7 days");
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
