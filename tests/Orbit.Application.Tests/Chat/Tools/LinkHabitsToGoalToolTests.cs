using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class LinkHabitsToGoalToolTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly LinkHabitsToGoalTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public LinkHabitsToGoalToolTests()
    {
        _tool = new LinkHabitsToGoalTool(_goalRepo, _habitRepo, _unitOfWork);
    }

    [Fact]
    public async Task LinkHabits_ReturnsSuccessAndSaves()
    {
        var goal = Goal.Create(UserId, "Be Healthy", 1, "goal").Value;
        var habit = CreateHabit("Run");
        SetupGoalFound(goal);
        SetupHabitsFound(habit);

        var result = await Execute($$$"""{"goal_id": "{{{goal.Id}}}", "habit_ids": ["{{{habit.Id}}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Be Healthy");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoalNotFound_ReturnsError()
    {
        var goalId = Guid.NewGuid();
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Goal?)null);

        var habitId = Guid.NewGuid();
        var result = await Execute($$$"""{"goal_id": "{{{goalId}}}", "habit_ids": ["{{{habitId}}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be(ErrorMessages.GoalNotFound);
    }

    [Fact]
    public async Task HabitNotFound_LinksOnlyFoundHabits()
    {
        var goal = Goal.Create(UserId, "Be Healthy", 1, "goal").Value;
        SetupGoalFound(goal);

        // No habits found
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(new List<Habit>());

        var missingId = Guid.NewGuid();
        var result = await Execute($$$"""{"goal_id": "{{{goal.Id}}}", "habit_ids": ["{{{missingId}}}"]}""");

        // Still succeeds, just no habits linked
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task MissingGoalId_ReturnsError()
    {
        var habitId = Guid.NewGuid();
        var result = await Execute($$$"""{"habit_ids": ["{{{habitId}}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("goal_id is required");
    }

    [Fact]
    public async Task MissingHabitIds_ReturnsError()
    {
        var goalId = Guid.NewGuid();
        var result = await Execute($$$"""{"goal_id": "{{{goalId}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids is required");
    }

    [Fact]
    public async Task TooManyHabits_ReturnsError()
    {
        var goalId = Guid.NewGuid();
        // Create more than MaxHabitsPerGoal (20) habit IDs
        var ids = Enumerable.Range(0, AppConstants.MaxHabitsPerGoal + 1)
            .Select(_ => $"\"{Guid.NewGuid()}\"");
        var idsJson = string.Join(",", ids);

        var result = await Execute($$$"""{"goal_id": "{{{goalId}}}", "habit_ids": [{{{idsJson}}}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("at most");
    }

    [Fact]
    public async Task ReplacesExistingLinks()
    {
        var goal = Goal.Create(UserId, "Be Healthy", 1, "goal").Value;
        var oldHabit = CreateHabit("Walk");
        goal.AddHabit(oldHabit);
        SetupGoalFound(goal);

        var newHabit = CreateHabit("Run");
        SetupHabitsFound(newHabit);

        var result = await Execute($$$"""{"goal_id": "{{{goal.Id}}}", "habit_ids": ["{{{newHabit.Id}}}"]}""");

        result.Success.Should().BeTrue();
        // Old habit should have been removed and new one added
        goal.Habits.Should().Contain(newHabit);
        goal.Habits.Should().NotContain(oldHabit);
    }

    private static Habit CreateHabit(string title)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today)).Value;
    }

    private void SetupGoalFound(Goal goal)
    {
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(goal);
    }

    private void SetupHabitsFound(params Habit[] habits)
    {
        _habitRepo.FindTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>()
        ).Returns(habits.ToList());
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
