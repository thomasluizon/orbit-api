using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public partial class HabitSuggestionTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"count\":null}")]
    [InlineData("{\"count\":0}")]
    [InlineData("{\"count\":6}")]
    [InlineData("{\"count\":\"1\"}")]
    [InlineData("{\"count\":1.5}")]
    [InlineData("{\"count\":2147483648}")]
    public async Task SuggestTool_RejectsMissingOrInvalidCountBeforeDispatch(string json)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new HabitSuggestionsCard("en", [Draft()])));

        var result = await new SuggestHabitsTool(mediator).ExecuteAsync(JsonSerializer.Deserialize<JsonElement>(json), _userId, default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("HABIT_SUGGESTION_INVALID");
        result.Error.Should().Be(ErrorCopy.Resolve("HABIT_SUGGESTION_INVALID", false));
        await mediator.DidNotReceive().Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(HabitSuggestionsCard.MaxDrafts)]
    public async Task SuggestTool_DispatchesTheExplicitCount(int count)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new HabitSuggestionsCard("en", [Draft()])));

        var result = await new SuggestHabitsTool(mediator).ExecuteAsync(JsonSerializer.SerializeToElement(new { count }), _userId, default);

        result.Success.Should().BeTrue();
        await mediator.Received(1).Send(Arg.Is<SuggestHabitDraftsQuery>(query => query.Count == count), Arg.Any<CancellationToken>());
    }
}
