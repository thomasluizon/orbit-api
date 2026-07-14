using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class ReorderHabitsToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ReorderHabitsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public ReorderHabitsToolTests() => _tool = new ReorderHabitsTool(_mediator);

    [Fact]
    public void Metadata_IsExposed()
    {
        _tool.Name.Should().Be("reorder_habits");
        _tool.GetParameterSchema().Should().NotBeNull();
    }

    [Fact]
    public async Task MissingPositions_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("positions is required");
    }

    [Fact]
    public async Task PositionsNotArray_ReturnsError()
    {
        var result = await Execute("""{"positions": 3}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("positions is required");
    }

    [Fact]
    public async Task ItemMissingPosition_ReturnsError()
    {
        var result = await Execute($$"""{"positions": [{"habit_id": "{{Guid.NewGuid()}}"}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("valid habit_id GUID and an integer position");
    }

    [Fact]
    public async Task ItemWithInvalidGuid_ReturnsError()
    {
        var result = await Execute("""{"positions": [{"habit_id": "bad", "position": 0}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("valid habit_id GUID");
    }

    [Fact]
    public async Task ValidPositions_ForwardsCommand_ReturnsSuccess()
    {
        ReorderHabitsCommand? captured = null;
        _mediator.Send(Arg.Any<ReorderHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { captured = callInfo.Arg<ReorderHabitsCommand>(); return Result.Success(); });
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var result = await Execute(
            $$"""{"positions": [{"habit_id": "{{first}}", "position": 0}, {"habit_id": "{{second}}", "position": 1}]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("2 habits");
        captured!.Positions.Should().HaveCount(2);
        captured.Positions[1].HabitId.Should().Be(second);
        captured.Positions[1].Position.Should().Be(1);
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        _mediator.Send(Arg.Any<ReorderHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Some habits do not belong to you."));

        var result = await Execute($$"""{"positions": [{"habit_id": "{{Guid.NewGuid()}}", "position": 0}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Some habits do not belong to you.");
    }

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}
