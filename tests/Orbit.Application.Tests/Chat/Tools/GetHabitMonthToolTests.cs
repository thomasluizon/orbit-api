using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class GetHabitMonthToolTests
{
    [Theory]
    [InlineData("{\"date_from\":\"next month\"}")]
    [InlineData("{\"date_to\":17}")]
    [InlineData("[]")]
    public async Task InvalidDateArgumentsFailWithoutDispatchingARead(string json)
    {
        var mediator = Substitute.For<IMediator>();
        var result = await new GetHabitMonthTool(mediator).ExecuteAsync(JsonDocument.Parse(json).RootElement,
            Guid.NewGuid(), default);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("HABIT_CALENDAR_RANGE_INVALID");
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task OmittedArgumentsDeferLocalMonthResolutionToQuery()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitMonthQuery>(), Arg.Any<CancellationToken>())
            .Returns(Orbit.Domain.Common.Result.Failure<Orbit.Application.Chat.HabitMonthCard>("source", "SOURCE"));
        var user = Guid.NewGuid();
        var result = await new GetHabitMonthTool(mediator).ExecuteAsync(JsonDocument.Parse("{}").RootElement, user, default);
        result.ErrorCode.Should().Be("SOURCE");
        await mediator.Received(1).Send(Arg.Is<GetHabitMonthQuery>(query => query.UserId == user
            && query.DateFrom == null && query.DateTo == null && query.Language == "en"), Arg.Any<CancellationToken>());
    }
}
