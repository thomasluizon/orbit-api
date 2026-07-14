using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class LinkGoalsToHabitToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly LinkGoalsToHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public LinkGoalsToHabitToolTests() => _tool = new LinkGoalsToHabitTool(_mediator);

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
        var habitId = Guid.NewGuid();
        var goalId = Guid.NewGuid();

        var result = await Execute($$"""{"habit_id": "{{habitId}}", "goal_ids": ["{{goalId}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(habitId.ToString());
        captured!.GoalIds.Should().ContainSingle().Which.Should().Be(goalId);
    }

    [Fact]
    public async Task EmptyGoalIds_UnlinksAll_ReturnsSuccess()
    {
        LinkGoalsToHabitCommand? captured = null;
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { captured = callInfo.Arg<LinkGoalsToHabitCommand>(); return Result.Success(); });

        var result = await Execute($$"""{"habit_id": "{{Guid.NewGuid()}}", "goal_ids": []}""");

        result.Success.Should().BeTrue();
        captured!.GoalIds.Should().BeEmpty();
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit not found."));

        var result = await Execute($$"""{"habit_id": "{{Guid.NewGuid()}}", "goal_ids": ["{{Guid.NewGuid()}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Habit not found.");
    }

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}
