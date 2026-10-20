using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class MoveHabitParentToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly Habit _habit = HabitToolTestFactory.CreateHabit(UserId, "Floss");
    private readonly MoveHabitParentTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public MoveHabitParentToolTests()
    {
        _tool = new MoveHabitParentTool(_mediator, _habitRepo);
        HabitToolTestFactory.SetupHabitFound(_habitRepo, _habit);
    }

    [Fact]
    public void Metadata_IsExposed()
    {
        _tool.Name.Should().Be("move_habit_parent");
        _tool.GetParameterSchema().Should().NotBeNull();
    }

    [Fact]
    public async Task MissingHabitId_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task InvalidHabitId_ReturnsError()
    {
        var result = await Execute("""{"habit_id": "not-a-guid"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task InvalidParentId_ReturnsError()
    {
        var result = await Execute($$"""{"habit_id": "{{_habit.Id}}", "parent_id": "nope"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("parent_id must be a valid GUID");
        await _mediator.DidNotReceive().Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteToTopLevel_SendsNullParent_ReturnsSuccess()
    {
        MoveHabitParentCommand? captured = null;
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { captured = callInfo.Arg<MoveHabitParentCommand>(); return Result.Success(); });
        var habitId = _habit.Id;

        var result = await Execute($$"""{"habit_id": "{{habitId}}"}""");

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(habitId.ToString());
        result.EntityName.Should().Be("Floss");
        captured!.ParentId.Should().BeNull();
        captured.UserId.Should().Be(UserId);
    }

    [Fact]
    public async Task Reparent_ForwardsParentId()
    {
        MoveHabitParentCommand? captured = null;
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { captured = callInfo.Arg<MoveHabitParentCommand>(); return Result.Success(); });
        var habitId = _habit.Id;
        var parentId = Guid.NewGuid();

        var result = await Execute($$"""{"habit_id": "{{habitId}}", "parent_id": "{{parentId}}"}""");

        result.Success.Should().BeTrue();
        captured!.ParentId.Should().Be(parentId);
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Cannot create a cycle."));

        var result = await Execute($$"""{"habit_id": "{{_habit.Id}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Cannot create a cycle.");
        result.EntityName.Should().BeNull();
    }

    [Fact]
    public async Task MissingHabit_ReturnsNotFoundWithoutDispatch()
    {
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var id = Guid.NewGuid();
        var result = await Execute($$"""{"habit_id":"{{id}}"}""");
        result.Success.Should().BeFalse();
        result.Error.Should().Be($"Habit {id} not found.");
        await _mediator.DidNotReceive().Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForeignHabit_LookupRejectsCallerAndDoesNotDispatch()
    {
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var foreign = HabitToolTestFactory.CreateHabit(Guid.NewGuid(), "Private title");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, foreign);
        var result = await Execute($$"""{"habit_id":"{{foreign.Id}}"}""");
        result.Success.Should().BeFalse();
        result.EntityName.Should().BeNull();
        await _mediator.DidNotReceive().Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>());
    }

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}
