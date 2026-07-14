using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class BulkCreateHabitsToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly BulkCreateHabitsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();

    public BulkCreateHabitsToolTests() => _tool = new BulkCreateHabitsTool(_mediator);

    [Fact]
    public void Metadata_And_Schema_AreExposed()
    {
        _tool.Name.Should().Be("bulk_create_habits");
        _tool.Description.Should().NotBeNullOrWhiteSpace();
        _tool.GetParameterSchema().Should().NotBeNull();
    }

    [Fact]
    public async Task MissingHabits_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habits is required");
    }

    [Fact]
    public async Task HabitsNotArray_ReturnsError()
    {
        var result = await Execute("""{"habits": "nope"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habits is required");
    }

    [Fact]
    public async Task HabitWithoutTitle_ReturnsError()
    {
        var result = await Execute("""{"habits": [{"description": "no title here"}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("non-empty title");
        await _mediator.DidNotReceive().Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyArray_ReturnsNoHabitsError()
    {
        var result = await Execute("""{"habits": []}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No habits provided");
    }

    [Fact]
    public async Task ValidHabitsWithSubHabits_ReportsSuccessCount()
    {
        BulkCreateHabitsCommand? captured = null;
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<BulkCreateHabitsCommand>();
                return Result.Success(new BulkCreateResult(new[]
                {
                    new BulkCreateItemResult(0, BulkItemStatus.Success, Guid.NewGuid(), "Morning routine"),
                    new BulkCreateItemResult(1, BulkItemStatus.Failed, null, "Gym", "duplicate"),
                }));
            });

        var result = await Execute("""
            {"habits": [
              {"title": "Morning routine", "frequency_unit": "day", "frequency_quantity": 1,
               "sub_habits": [{"title": "Make bed"}, {"description": "child missing title"}]},
              {"title": "Gym", "is_bad_habit": false}
            ]}
            """);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("1/2 habits created");
        result.Payload.Should().BeOfType<BulkCreateResult>();
        captured!.Habits.Should().HaveCount(2);
        captured.Habits[0].SubHabits.Should().ContainSingle().Which.Title.Should().Be("Make bed");
    }

    [Fact]
    public async Task CommandFails_PropagatesError()
    {
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkCreateResult>("Habit limit reached."));

        var result = await Execute("""{"habits": [{"title": "Read"}]}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Habit limit reached.");
    }

    private async Task<ToolResult> Execute(string json) =>
        await _tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, UserId, CancellationToken.None);
}
