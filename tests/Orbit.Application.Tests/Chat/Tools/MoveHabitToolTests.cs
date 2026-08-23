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

public class MoveHabitToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly MoveHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public MoveHabitToolTests()
    {
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _tool = new MoveHabitTool(_mediator, _habitRepo);
    }

    [Fact]
    public async Task MoveUnderParent_RoutesToGuardedCommand()
    {
        var child = CreateHabit("Floss");
        var parent = CreateHabit("Before Bed");
        SetupFindOneTrackedSingle(child);

        var result = await Execute($$$"""{"habit_id": "{{{child.Id}}}", "new_parent_id": "{{{parent.Id}}}"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Floss");
        await _mediator.Received(1).Send(
            Arg.Is<MoveHabitParentCommand>(command =>
                command.UserId == UserId
                && command.HabitId == child.Id
                && command.ParentId == parent.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteToTopLevel_RoutesToGuardedCommand()
    {
        var child = CreateHabit("Floss");
        child.SetParentHabitId(Guid.NewGuid());
        SetupFindOneTrackedSingle(child);

        var result = await Execute($$$"""{"habit_id": "{{{child.Id}}}"}""");

        result.Success.Should().BeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<MoveHabitParentCommand>(command => command.ParentId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitNotFound_ReturnsError()
    {
        var id = Guid.NewGuid();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Habit?)null);

        var result = await Execute($$$"""{"habit_id": "{{{id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task ParentNotFound_ReturnsError()
    {
        var child = CreateHabit("Floss");
        var missingParentId = Guid.NewGuid();

        SetupFindOneTrackedSingle(child);
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Target parent habit not found."));

        var result = await Execute($$$"""{"habit_id": "{{{child.Id}}}", "new_parent_id": "{{{missingParentId}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task SelfReference_ReturnsError()
    {
        var habit = CreateHabit("Water");
        SetupFindOneTrackedSingle(habit);

        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("A habit cannot be its own parent."));

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}", "new_parent_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("own parent");
    }

    [Fact]
    public async Task MissingHabitId_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task WrongUser_CannotDispatchMove()
    {
        var child = CreateHabit("Owner child");
        SetupFindOneTrackedSingle(child);
        var attackerId = Guid.NewGuid();
        _habitRepo.FindOneTrackedAsync(
                Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
                Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var attackerResult = await _tool.ExecuteAsync(
            PromoteArgs(child.Id), attackerId, CancellationToken.None);

        attackerResult.Success.Should().BeFalse();
        attackerResult.Error.Should().Contain("not found");
        await _mediator.DidNotReceive().Send(
            Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteAtCeiling_ReturnsNeutralFailureWithoutMutatingChild()
    {
        var parentId = Guid.NewGuid();
        var child = CreateHabit("Floss");
        child.SetParentHabitId(parentId);
        SetupFindOneTrackedSingle(child);
        _mediator.Send(
                Arg.Is<MoveHabitParentCommand>(command => command.ParentId == null),
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure("You've reached the 1000 habit limit."));

        var result = await Execute($$$"""{"habit_id": "{{{child.Id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("You've reached the 1000 habit limit.");
        result.ErrorCode.Should().BeNull();
        child.ParentHabitId.Should().Be(parentId);
    }

    private static JsonElement PromoteArgs(Guid habitId) =>
        JsonDocument.Parse($$"""{"habit_id":"{{habitId}}"}""").RootElement;

    private static Habit CreateHabit(string title)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today)).Value;
    }

    private void SetupFindOneTrackedSingle(Habit habit)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(habit);
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
