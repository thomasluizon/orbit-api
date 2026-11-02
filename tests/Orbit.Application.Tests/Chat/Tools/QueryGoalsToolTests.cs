using System.Globalization;
using System.Linq.Expressions;
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
    private readonly IGoalProgressReadSyncer _goalProgressReadSyncer = Substitute.For<IGoalProgressReadSyncer>();
    private readonly QueryGoalsTool _tool;
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public QueryGoalsToolTests()
    {
        _tool = new QueryGoalsTool(_goalRepository, _userDateService, _goalProgressReadSyncer);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        _goalProgressReadSyncer.ComputeFreshValuesForGoalsAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
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

        SetupGoals([activeGoal]);

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Contain("Run a marathon");
        result.EntityName.Should().Contain("Train for race day");
    }

    [Fact]
    public async Task ExecuteAsync_DecimalProgress_UsesInvariantCulture()
    {
        var goal = Goal.Create(UserId, "Run", 10.5m, "miles").Value;
        goal.UpdateProgress(3.5m);
        SetupGoals([goal]);
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
            var result = await Execute("{}");

            result.Success.Should().BeTrue();
            result.EntityName.Should().Contain("Progress: 3.5/10.5 miles");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task ExecuteAsync_Deadline_UsesInvariantCalendar()
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Run", 10, "miles", "Race training", Deadline: new DateOnly(2026, 12, 31))).Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, "Morning run", FrequencyUnit.Day, 1, DueDate: Today)).Value;
        goal.AddHabit(habit);
        SetupGoals([goal]);
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var result = await Execute("{}");

            result.EntityName.Should().Contain("Deadline: 2026-12-31");
            result.EntityName.Should().Contain("Description: Race training");
            result.EntityName.Should().Contain($"Linked habits: Morning run ({habit.Id})");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsNoGoalsMessage_WhenSearchMisses()
    {
        var activeGoal = Goal.Create(new Goal.CreateGoalParams(
            UserId,
            "Run a marathon",
            42,
            "km")).Value;

        SetupGoals([activeGoal]);

        var result = await Execute("""{"search":"books"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("No goals found matching the given filters.");
    }

    [Fact]
    public async Task ExecuteAsync_ReflectsFreshStreakValue_FromSyncerComputedBeforeRead()
    {
        var streakGoal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Avoid doom scrolling", 7, "days", Type: GoalType.Streak)).Value;

        _goalProgressReadSyncer
            .ComputeFreshValuesForGoalsAsync(UserId, Today, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [streakGoal.Id] = 4 });

        SetupGoals([streakGoal]);

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        await _goalProgressReadSyncer.Received(1).ComputeFreshValuesForGoalsAsync(UserId, Today, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        result.EntityName.Should().Contain("4/7 days");
    }

    private void SetupGoals(IReadOnlyList<Goal> goals)
    {
        _goalRepository.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<IQueryable<Goal>, IQueryable<Goal>>>(1)(
                goals.Where(call.ArgAt<Expression<Func<Goal, bool>>>(0).Compile()).AsQueryable()).ToArray());
        _goalRepository.CountAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => goals.Count(call.ArgAt<Expression<Func<Goal, bool>>>(0).Compile()));
    }

    [Fact]
    public async Task LimitTen_RecomputesOnlyTheReturnedPage()
    {
        var goals = Enumerable.Range(0, 100).Select(i => Goal.Create(new Goal.CreateGoalParams(UserId, "Goal " + i, 10, "times")).Value).ToArray();
        SetupGoals(goals);
        var result = await Execute("{\"offset\":10,\"limit\":10}");
        var card = (Orbit.Application.Chat.GoalListCard)result.Payload!;
        card.Items.Should().HaveCount(10);
        card.TotalCount.Should().Be(100);
        var ids = card.Items.Select(item => Guid.Parse(item.Id)).ToHashSet();
        await _goalProgressReadSyncer.Received(1).ComputeFreshValuesForGoalsAsync(UserId, Today,
            Arg.Is<IReadOnlyCollection<Guid>>(values => values.Count == 10 && values.All(ids.Contains)), Arg.Any<CancellationToken>());
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
