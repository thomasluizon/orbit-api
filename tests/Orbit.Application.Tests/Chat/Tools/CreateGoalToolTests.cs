using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class CreateGoalToolTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CreateGoalTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public CreateGoalToolTests()
    {
        _tool = new CreateGoalTool(Orbit.Tests.Shared.GoalToolCommandMediator.Create(_goalRepo, _unitOfWork, habits: _habitRepo));
    }

    [Theory]
    [MemberData(nameof(OptionalParameterCases.Fields), typeof(CreateGoalTool), "", MemberType = typeof(OptionalParameterCases))]
    public async Task OptionalNull_CreatesSameGoalAsAbsent(string field)
    {
        var saved = new List<Goal>();
        await _goalRepo.AddAsync(Arg.Do<Goal>(saved.Add), Arg.Any<CancellationToken>());
        (await Execute("""{"title":"Read"}""")).Success.Should().BeTrue();
        (await Execute($$"""{"title":"Read","{{field}}":null}""")).Success.Should().BeTrue();
        saved.Should().HaveCount(2);
        saved[1].Should().BeEquivalentTo(saved[0], options => options.ComparingByMembers<Goal>()
            .Excluding(goal => goal.Id).Excluding(goal => goal.CreatedAtUtc).Excluding(goal => goal.UpdatedAtUtc));
    }

    [Fact]
    public async Task SuccessfulCreation_ReturnsSuccessWithTitle()
    {
        var result = await Execute("""{"title": "Read 12 books"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Read 12 books");
        result.EntityId.Should().NotBeNullOrEmpty();
        await _goalRepo.Received(1).AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingTitle_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title is required");
    }

    [Fact]
    public async Task EmptyTitle_ReturnsError()
    {
        var result = await Execute("""{"title": "  "}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title is required");
    }

    [Fact]
    public async Task WithTargetAndUnit_CreatesGoalCorrectly()
    {
        var result = await Execute("""{"title": "Read books", "target_value": 12, "unit": "books"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Read books");
    }

    [Fact]
    public async Task WithDeadline_CreatesGoalWithDeadline()
    {
        var result = await Execute("""{"title": "Lose weight", "target_value": 5, "unit": "kg", "deadline": "2026-12-31"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Lose weight");
    }

    [Fact]
    public async Task DefaultTargetValue_UsesOne()
    {
        var result = await Execute("""{"title": "Complete goal"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Complete goal");
    }

    [Fact]
    public async Task WithDescription_CreatesGoalWithDescription()
    {
        var result = await Execute("""{"title": "Save money", "description": "For vacation fund", "target_value": 5000, "unit": "dollars"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Save money");
    }

    [Fact]
    public async Task HabitIds_AboveCap_ReturnsExistingErrorWithoutWritingGoalOrLinks()
    {
        var habits = SetUpHabits(AppConstants.MaxHabitsPerGoal + 1);

        var result = await Execute(JsonSerializer.Serialize(new
        {
            title = "Read books",
            habit_ids = habits.Select(habit => habit.Id)
        }));

        result.Success.Should().BeFalse();
        result.Error.Should().Be(ErrorMessages.MaxHabitsPerGoal.Format(AppConstants.MaxHabitsPerGoal).Message);
        result.ErrorCode.Should().Be(ErrorMessages.MaxHabitsPerGoal.Code);
        result.EntityId.Should().BeNull();
        await _goalRepo.DidNotReceive().AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        habits.Should().OnlyContain(habit => habit.Goals.Count == 0);
    }

    [Fact]
    public async Task HabitIds_AtCap_CreatesGoalWithEveryHabitLinked()
    {
        var habits = SetUpHabits(AppConstants.MaxHabitsPerGoal);

        var result = await Execute(JsonSerializer.Serialize(new
        {
            title = "Read books",
            habit_ids = habits.Select(habit => habit.Id)
        }));

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(goal => goal.Id.ToString() == result.EntityId && goal.Habits.Count == habits.Count
                && goal.Habits.All(habit => habits.Contains(habit))),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        foreach (var habit in habits)
            habit.Goals.Should().ContainSingle().Which.Id.ToString().Should().Be(result.EntityId);
    }

    private List<Habit> SetUpHabits(int count)
    {
        var habits = Enumerable.Range(0, count)
            .Select(index => Habit.Create(new HabitCreateParams(
                UserId, $"Read habit {index}", null, null, new DateOnly(2026, 4, 3))).Value)
            .ToList();
        _habitRepo.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(habits);
        return habits;
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
