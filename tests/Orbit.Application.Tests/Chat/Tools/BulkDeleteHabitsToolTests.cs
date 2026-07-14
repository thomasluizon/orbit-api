using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class BulkDeleteHabitsToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly BulkDeleteHabitsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public BulkDeleteHabitsToolTests() => _tool = new BulkDeleteHabitsTool(_mediator);

    [Fact]
    public void Metadata_IsExposed()
    {
        _tool.Name.Should().Be("bulk_delete_habits");
        _tool.GetParameterSchema().Should().NotBeNull();
    }

    [Fact]
    public async Task MissingHabitIds_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids is required");
    }

    [Fact]
    public async Task HabitIdsNotArray_ReturnsError()
    {
        var result = await Execute("""{"habit_ids": "x"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_ids is required");
    }

    [Fact]
    public async Task EmptyArray_ReturnsNoValidIdsError()
    {
        var result = await Execute("""{"habit_ids": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No valid habit IDs");
    }

    [Fact]
    public async Task AllInvalidIds_ReturnsNoValidIdsError()
    {
        var result = await Execute("""{"habit_ids": ["nope", "still-not"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No valid habit IDs");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidIds_ReportsSuccessCount()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _mediator.Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BulkDeleteResult(new[]
            {
                new BulkDeleteItemResult(0, BulkItemStatus.Success, first),
                new BulkDeleteItemResult(1, BulkItemStatus.Failed, second, "in use"),
            })));

        var result = await Execute($$"""{"habit_ids": ["{{first}}", "{{second}}"]}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("1/2 habits deleted");
        result.Payload.Should().BeOfType<BulkDeleteResult>();
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        _mediator.Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkDeleteResult>("Too many habits."));

        var result = await Execute($$"""{"habit_ids": ["{{Guid.NewGuid()}}"]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Too many habits.");
    }

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}
