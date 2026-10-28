using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class BulkRescheduleHabitsToolTests
{
    [Fact]
    public async Task IndividualDates_AreExposedToTheModelAndUseExistingCommands()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkUpdateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BulkHabitMutationResult(1, 1, 0, false)));
        var user = Guid.NewGuid();
        var keep = Guid.NewGuid();
        var move = Guid.NewGuid();
        var args = JsonSerializer.SerializeToElement(new
        {
            revised_items = new[]
            {
                new { habit_id = keep, updates = new { due_date = "2026-04-03" } },
                new { habit_id = move, updates = new { due_date = "2026-04-04" } }
            }
        });
        var tool = new BulkRescheduleHabitsTool(mediator);

        ChatToolArgumentCheck.HasValidShape(tool, args).Should().BeTrue();
        var result = await tool.ExecuteAsync(args, user, default);

        result.Success.Should().BeTrue(result.Error);
        await mediator.Received(1).Send(Arg.Is<BulkUpdateHabitsCommand>(command =>
            command.UserId == user && command.Filter.HabitIds.SequenceEqual(new[] { keep })
            && command.Changes.HasDueDate && command.Changes.DueDate == new DateOnly(2026, 4, 3)), Arg.Any<CancellationToken>());
        await mediator.Received(1).Send(Arg.Is<BulkUpdateHabitsCommand>(command =>
            command.UserId == user && command.Filter.HabitIds.SequenceEqual(new[] { move })
            && command.Changes.HasDueDate && command.Changes.DueDate == new DateOnly(2026, 4, 4)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[{\"habit_id\":\"invalid\",\"updates\":{\"due_date\":\"2026-04-04\"}}]")]
    [InlineData("[{\"habit_id\":\"11111111-1111-1111-1111-111111111111\",\"updates\":{\"due_date\":\"invalid\"}}]")]
    [InlineData("[{\"habit_id\":\"11111111-1111-1111-1111-111111111111\",\"updates\":{}}]")]
    public async Task InvalidPlan_IsRejectedBeforeAnyWrite(string items)
    {
        var mediator = Substitute.For<IMediator>();
        var args = JsonDocument.Parse("{\"revised_items\":" + items + "}").RootElement;

        var result = await new BulkRescheduleHabitsTool(mediator).CheckArgumentsAsync(args, Guid.NewGuid(), default);

        result.IsFailure.Should().BeTrue();
        mediator.ReceivedCalls().Should().BeEmpty();
    }
}
