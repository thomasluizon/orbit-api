using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class CreateSubHabitToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly CreateSubHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ParentId = Guid.NewGuid();

    public CreateSubHabitToolTests()
    {
        _tool = new CreateSubHabitTool(_mediator);
    }

    [Fact]
    public async Task SuccessfulCreation_ReturnsSuccessWithId()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var result = await Execute($$$"""{"parent_habit_id": "{{{ParentId}}}", "title": "Floss"}""");

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(newId.ToString());
        result.EntityName.Should().Be("Floss");
    }

    [Fact]
    public async Task ParentNotFound_ReturnsError()
    {
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Parent habit not found."));

        var result = await Execute($$$"""{"parent_habit_id": "{{{ParentId}}}", "title": "Floss"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task MissingParentId_ReturnsError()
    {
        var result = await Execute("""{"title": "Floss"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("parent_habit_id is required");
    }

    [Fact]
    public async Task MissingTitle_ReturnsError()
    {
        var result = await Execute($$$"""{"parent_habit_id": "{{{ParentId}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("title is required");
    }

    [Fact]
    public async Task WithSchedule_SendsCommandWithSchedule()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var result = await Execute($$$"""
        {
            "parent_habit_id": "{{{ParentId}}}",
            "title": "Floss",
            "frequency_unit": "Day",
            "frequency_quantity": 1,
            "due_time": "21:00"
        }
        """);

        result.Success.Should().BeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<CreateSubHabitCommand>(cmd =>
                cmd.Title == "Floss" &&
                cmd.FrequencyUnit == Domain.Enums.FrequencyUnit.Day &&
                cmd.FrequencyQuantity == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithBooleanFlags_ParsesCorrectly()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var result = await Execute($$$"""
        {
            "parent_habit_id": "{{{ParentId}}}",
            "title": "Smoke less",
            "is_bad_habit": true,
            "reminder_enabled": true
        }
        """);

        result.Success.Should().BeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<CreateSubHabitCommand>(cmd =>
                cmd.IsBadHabit == true),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidParentGuid_ReturnsError()
    {
        var result = await Execute("""{"parent_habit_id": "not-a-guid", "title": "Sub"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("parent_habit_id is required");
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
