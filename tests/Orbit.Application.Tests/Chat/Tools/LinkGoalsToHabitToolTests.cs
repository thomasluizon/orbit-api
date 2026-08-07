using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class LinkGoalsToHabitToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly LinkGoalsToHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public LinkGoalsToHabitToolTests() => _tool = new LinkGoalsToHabitTool(_mediator, _habitRepo);

    [Fact]
    public void Metadata_IsExposed()
    {
        _tool.Name.Should().Be("link_goals_to_habit");
        _tool.GetParameterSchema().Should().NotBeNull();
    }

    [Fact]
    public async Task MissingHabitId_ReturnsError()
    {
        var result = await Execute("""{"goal_ids": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task InvalidHabitId_ReturnsError()
    {
        var result = await Execute("""{"habit_id": "x", "goal_ids": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task MissingGoalIds_ReturnsError()
    {
        var result = await Execute($$"""{"habit_id": "{{Guid.NewGuid()}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("goal_ids is required");
    }

    [Fact]
    public async Task GoalIdsNotArray_ReturnsError()
    {
        var result = await Execute($$"""{"habit_id": "{{Guid.NewGuid()}}", "goal_ids": "nope"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("goal_ids is required");
    }

    [Fact]
    public async Task LinkGoals_ForwardsCommand_ReturnsSuccess()
    {
        LinkGoalsToHabitCommand? captured = null;
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { captured = callInfo.Arg<LinkGoalsToHabitCommand>(); return Result.Success(); });
        var habit = CreateHabit("Run 5K");
        SetupHabitFound(habit);
        var goalId = Guid.NewGuid();

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}", "goal_ids": ["{{goalId}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(habit.Id.ToString());
        result.EntityName.Should().Be("Run 5K");
        captured!.GoalIds.Should().ContainSingle().Which.Should().Be(goalId);
    }

    [Fact]
    public async Task EmptyGoalIds_UnlinksAll_ReturnsSuccess()
    {
        LinkGoalsToHabitCommand? captured = null;
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { captured = callInfo.Arg<LinkGoalsToHabitCommand>(); return Result.Success(); });
        var habit = CreateHabit("Run 5K");
        SetupHabitFound(habit);

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}", "goal_ids": []}""");

        result.Success.Should().BeTrue();
        captured!.GoalIds.Should().BeEmpty();
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        var habit = CreateHabit("Run 5K");
        SetupHabitFound(habit);
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit not found."));

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}", "goal_ids": ["{{Guid.NewGuid()}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Habit not found.");
        result.EntityName.Should().BeNull();
    }

    private void SetupHabitFound(Habit habit) =>
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
        .Returns(habit);

    private static Habit CreateHabit(string title) =>
        Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, new DateOnly(2026, 8, 6))).Value;

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}
