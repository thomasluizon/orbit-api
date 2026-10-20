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

public class DuplicateHabitToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly DuplicateHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public DuplicateHabitToolTests()
    {
        _tool = new DuplicateHabitTool(_mediator, _habitRepo);
    }

    [Fact]
    public async Task SuccessfulDuplicate_ReturnsSuccessWithNewId()
    {
        var habitId = Guid.NewGuid();
        var duplicate = HabitToolTestFactory.CreateHabit(UserId, "Read books");
        var newId = duplicate.Id;
        _mediator.Send(Arg.Any<DuplicateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));
        HabitToolTestFactory.SetupHabitFound(_habitRepo, duplicate);

        var result = await Execute($$$"""{"habit_id": "{{{habitId}}}"}""");

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(newId.ToString());
        result.EntityName.Should().Be("Read books");
    }

    [Fact]
    public async Task SuccessfulDuplicate_WhenNewHabitCannotBeResolved_ReturnsNullEntityName()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<DuplicateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var result = await Execute($$$"""{"habit_id": "{{{Guid.NewGuid()}}}"}""");

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(newId.ToString());
        result.EntityName.Should().BeNull();
    }

    [Fact]
    public async Task HabitNotFound_ReturnsError()
    {
        var habitId = Guid.NewGuid();
        _mediator.Send(Arg.Any<DuplicateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Habit not found."));

        var result = await Execute($$$"""{"habit_id": "{{{habitId}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
        result.EntityName.Should().BeNull();
    }

    [Fact]
    public async Task MissingHabitId_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task InvalidGuid_ReturnsError()
    {
        var result = await Execute("""{"habit_id": "not-a-guid"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task ForeignHabit_LookupRejectsCallerAndDoesNotExposeName()
    {
        var foreign = HabitToolTestFactory.CreateHabit(Guid.NewGuid(), "Private title");
        HabitToolTestFactory.SetupHabitFound(_habitRepo, foreign);
        _mediator.Send(Arg.Any<DuplicateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(foreign.Id));

        var result = await Execute($$"""{"habit_id": "{{foreign.Id}}"}""");

        result.EntityName.Should().BeNull();
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }


}
