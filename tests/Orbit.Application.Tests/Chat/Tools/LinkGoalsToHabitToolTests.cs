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
using Orbit.Application.Common;

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
        var habit = HabitToolTestFactory.CreateHabit(UserId, "Run 5K");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, habit);
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
        var habit = HabitToolTestFactory.CreateHabit(UserId, "Run 5K");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, habit);

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}", "goal_ids": []}""");

        result.Success.Should().BeTrue();
        captured!.GoalIds.Should().BeEmpty();
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        var habit = HabitToolTestFactory.CreateHabit(UserId, "Run 5K");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, habit);
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorMessages.HabitNotFound));

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}", "goal_ids": ["{{Guid.NewGuid()}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be(ErrorMessages.HabitNotFound.Message);
        result.EntityName.Should().BeNull();
    }

    [Fact]
    public async Task ForeignHabit_LookupRejectsCallerAndDoesNotExposeName()
    {
        var foreign = HabitToolTestFactory.CreateHabit(Guid.NewGuid(), "Private title");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, foreign);

        var result = await Execute($$"""{"habit_id": "{{foreign.Id}}", "goal_ids": []}""");

        result.EntityName.Should().BeNull();
        result.Success.Should().BeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingHabit_ReturnsNotFoundWithoutDispatch()
    {
        var id = Guid.NewGuid();
        var result = await Execute($$"""{"habit_id":"{{id}}","goal_ids":[]}""");
        result.Success.Should().BeFalse();
        result.Error.Should().Be($"Habit {id} not found.");
        await _mediator.DidNotReceive().Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>());
    }

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}
