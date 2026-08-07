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

public class MoveHabitParentToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly MoveHabitParentTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public MoveHabitParentToolTests() => _tool = new MoveHabitParentTool(_mediator, _habitRepo);

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
        var result = await Execute($$"""{"habit_id": "{{Guid.NewGuid()}}", "parent_id": "nope"}""");

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
        var habit = CreateHabit("Floss");
        SetupHabitFound(habit);

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}"}""");

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(habit.Id.ToString());
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
        var habit = CreateHabit("Floss");
        SetupHabitFound(habit);
        var parentId = Guid.NewGuid();

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}", "parent_id": "{{parentId}}"}""");

        result.Success.Should().BeTrue();
        captured!.ParentId.Should().Be(parentId);
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        var habit = CreateHabit("Floss");
        SetupHabitFound(habit);
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Cannot create a cycle."));

        var result = await Execute($$"""{"habit_id": "{{habit.Id}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Cannot create a cycle.");
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
