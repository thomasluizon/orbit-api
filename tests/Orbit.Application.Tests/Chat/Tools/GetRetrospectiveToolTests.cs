using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class GetRetrospectiveToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetRetrospectiveTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 6, 4);

    public GetRetrospectiveToolTests()
    {
        _tool = new GetRetrospectiveTool(_mediator, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    [Fact]
    public async Task Success_ReturnsPayload()
    {
        var response = new RetrospectiveResponse("Last week you kept a 5-day streak.", FromCache: false);
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.Payload.Should().Be(response);
    }

    [Fact]
    public async Task PayGateFailure_PropagatesError()
    {
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<RetrospectiveResponse>("Upgrade to yearly Pro to use the retrospective."));

        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("yearly Pro");
    }

    [Fact]
    public async Task MonthPeriod_DerivesThirtyDayRange()
    {
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RetrospectiveResponse("ok", false)));

        await Execute("""{"period": "month"}""");

        await _mediator.Received(1).Send(
            Arg.Is<GetRetrospectiveQuery>(q =>
                q.UserId == UserId &&
                q.Period == "month" &&
                q.DateFrom == Today.AddDays(-30) &&
                q.DateTo == Today),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoPeriod_DefaultsToWeek()
    {
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new RetrospectiveResponse("ok", false)));

        await Execute("{}");

        await _mediator.Received(1).Send(
            Arg.Is<GetRetrospectiveQuery>(q =>
                q.Period == "week" &&
                q.DateFrom == Today.AddDays(-7) &&
                q.DateTo == Today),
            Arg.Any<CancellationToken>());
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
