using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class BulkUpdateHabitsToolRevisionTests
{
    [Fact]
    public async Task RevisedItems_ExecutesOnlySelectedHabitWithEditedValue()
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        var selectedId = Guid.NewGuid();
        var removedId = Guid.NewGuid();
        mediator.Send(Arg.Any<BulkUpdateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BulkHabitMutationResult(1, 1, 0, false)));
        var args = JsonDocument.Parse($$$"""
            {"filter":{"habit_ids":["{{{selectedId}}}","{{{removedId}}}"]},"updates":{"emoji":"OLD"},"revised_items":[{"habit_id":"{{{selectedId}}}","updates":{"emoji":"✅"}}]}
            """).RootElement.Clone();

        var result = await new BulkUpdateHabitsTool(mediator)
            .ExecuteAsync(args, userId, CancellationToken.None);

        result.Success.Should().BeTrue();
        await mediator.Received(1).Send(Arg.Is<BulkUpdateHabitsCommand>(command =>
            command.UserId == userId
            && command.Filter.HabitIds.SequenceEqual(new[] { selectedId })
            && !command.Filter.HabitIds.Contains(removedId)
            && command.Changes.Emoji == "✅"), Arg.Any<CancellationToken>());
    }
}
