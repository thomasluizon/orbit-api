using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Behaviors;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class GetHabitRankingToolTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"window\":\"rolling_months\",\"months\":0}")]
    [InlineData("{\"date_from\":\"2026-10-20\",\"date_to_exclusive\":\"2026-10-19\"}")]
    [InlineData("{\"query_token\":\"bound\",\"refresh\":true,\"months\":6}")]
    public async Task InvalidQuery_ReturnsToolFailureInsteadOfThrowingFromValidationPipeline(string json)
    {
        var mediator = Substitute.For<IMediator>();
        var validation = new ValidationBehavior<GetHabitRankingQuery, Result<HabitRankingResponse>>(
            [new GetHabitRankingQueryValidator()]);
        mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => validation.Handle(call.Arg<GetHabitRankingQuery>(),
                _ => Task.FromResult(Result.Failure<HabitRankingResponse>("handler reached")), call.Arg<CancellationToken>()));

        var result = await new GetHabitRankingTool(mediator).ExecuteAsync(
            JsonDocument.Parse(json).RootElement, Guid.NewGuid(), default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("HABIT_RANKING_INVALID");
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"months\":\"6\"}")]
    [InlineData("{\"date_from\":\"not-a-date\"}")]
    [InlineData("{\"habit_ids\":[\"not-a-guid\"]}")]
    public async Task MalformedArguments_ReturnFailureBeforeDispatch(string json)
    {
        var mediator = Substitute.For<IMediator>();
        var result = await new GetHabitRankingTool(mediator).ExecuteAsync(JsonDocument.Parse(json).RootElement, Guid.NewGuid(), default);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("HABIT_RANKING_INVALID");
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("rolling_months")]
    [InlineData("complete_months")]
    public async Task CalendarWindow_AndNullableOptionalFields_ReachTheDeterministicQuery(string window)
    {
        var mediator = Substitute.For<IMediator>();
        var user = Guid.NewGuid();
        mediator.Send(Arg.Any<GetHabitRankingQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitRankingResponse>("test query result", "HABIT_RANKING_INVALID"));
        var args = JsonSerializer.SerializeToElement(new
        {
            window,
            months = 6,
            date_from = (string?)null,
            date_to_exclusive = (string?)null,
            metric = (string?)null,
            habit_ids = (string[]?)null,
            page_size = (int?)null,
            query_token = (string?)null,
            cursor = (string?)null,
            refresh = (bool?)null
        });
        var tool = new GetHabitRankingTool(mediator);

        var result = await tool.ExecuteAsync(args, user, default);

        tool.IsReadOnly.Should().BeTrue();
        result.Error.Should().Be("test query result");
        await mediator.Received(1).Send(Arg.Is<GetHabitRankingQuery>(q => q.UserId == user
            && q.Window == window && q.Months == 6 && q.DateFrom == null && q.QueryToken == null
            && q.Refresh == false), Arg.Any<CancellationToken>());
    }
}
