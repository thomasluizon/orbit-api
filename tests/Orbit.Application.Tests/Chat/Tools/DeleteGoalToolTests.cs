using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class DeleteGoalToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task ExecuteAsync_SoftDeletesGoalThroughDeleteGoalCommand()
    {
        var goalId = Guid.NewGuid();
        _mediator.Send(Arg.Any<DeleteGoalCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await new DeleteGoalTool(_mediator).ExecuteAsync(Args(goalId), UserId, default);
        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(goalId.ToString());
        await _mediator.Received(1).Send(Arg.Is<DeleteGoalCommand>(c => c.UserId == UserId && c.GoalId == goalId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CommandReturnsGoalNotFound_ReturnsNotFoundResult()
    {
        var caller = Guid.NewGuid();
        var goalId = Guid.NewGuid();
        _mediator.Send(Arg.Is<DeleteGoalCommand>(c => c.UserId == caller && c.GoalId == goalId), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(ErrorMessages.GoalNotFound));
        var result = await new DeleteGoalTool(_mediator).ExecuteAsync(Args(goalId), caller, default);
        result.Success.Should().BeFalse();
        result.Error.Should().Be($"Goal {goalId} not found.");
    }

    private static JsonElement Args(Guid goalId) => JsonSerializer.SerializeToElement(new { goal_id = goalId });
}
