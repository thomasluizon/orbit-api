using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class UndoSkipHabitToolTests
{
    [Fact]
    public async Task Undo_DispatchesTheSharedCommandWithCallerAndSkipId()
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        var habitId = Guid.NewGuid();
        var skipId = Guid.NewGuid();
        mediator.Send(Arg.Any<UndoSkipHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var args = JsonSerializer.SerializeToElement(new { habit_id = habitId, skip_id = skipId });

        var result = await new UndoSkipHabitTool(mediator).ExecuteAsync(args, userId, CancellationToken.None);

        result.Success.Should().BeTrue();
        await mediator.Received(1).Send(new UndoSkipHabitCommand(userId, habitId, skipId), CancellationToken.None);
    }

    [Fact]
    public async Task Undo_PropagatesConflict()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<UndoSkipHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(DomainErrors.SkipUndoConflict));
        var args = JsonSerializer.SerializeToElement(new { habit_id = Guid.NewGuid(), skip_id = Guid.NewGuid() });

        var result = await new UndoSkipHabitTool(mediator).ExecuteAsync(args, Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(DomainErrors.SkipUndoConflict.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"habit_id\":\"invalid\",\"skip_id\":\"invalid\"}")]
    [InlineData("{\"habit_id\":\"55555555-5555-5555-5555-555555555555\"}")]
    [InlineData("{\"habit_id\":\"55555555-5555-5555-5555-555555555555\",\"skip_id\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task Undo_InvalidArguments_DoesNotDispatch(string json)
    {
        var mediator = Substitute.For<IMediator>();
        var result = await new UndoSkipHabitTool(mediator).ExecuteAsync(
            JsonDocument.Parse(json).RootElement, Guid.NewGuid(), CancellationToken.None);
        result.Success.Should().BeFalse();
        mediator.ReceivedCalls().Should().BeEmpty();
    }
}
