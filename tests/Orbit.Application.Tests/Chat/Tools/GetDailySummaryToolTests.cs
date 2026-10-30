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

public class GetDailySummaryToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly GetDailySummaryTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 6, 4);

    public GetDailySummaryToolTests()
    {
        _tool = new GetDailySummaryTool(_mediator, _userDateService);
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
    }

    [Fact]
    public void Description_OffersTheNarrativeForTodaysNextPendingStep()
    {
        _tool.Description.Should().Contain("today's next pending step")
            .And.Contain("For what to do next today, pass relative_period: today and generate_narrative: true")
            .And.NotContain("Use only when");
        var schema = JsonSerializer.SerializeToElement(_tool.GetParameterSchema());
        schema.GetProperty("properties").GetProperty("generate_narrative").GetProperty("description")
            .GetString().Should().Contain("true for a next-step recommendation today");
    }

    [Fact]
    public async Task Success_ReturnsPayload()
    {
        var response = new DailySummaryResponse("You completed 3 of 4 habits today.", "Log your evening walk next.", FromCache: false);
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await Execute("{}");

        result.Success.Should().BeTrue();
        result.Payload.Should().Be(response);
    }

    [Fact]
    public async Task PayGateFailure_PropagatesError()
    {
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<DailySummaryResponse>("Upgrade to Pro to use the daily summary."));

        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Upgrade to Pro");
    }

    [Fact]
    public async Task NoDates_DefaultsBothToUserToday()
    {
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DailySummaryResponse("ok", "", false)));

        await Execute("{}");

        await _mediator.Received(1).Send(
            Arg.Is<GetDailySummaryQuery>(q =>
                q.UserId == UserId &&
                q.DateFrom == Today &&
                q.DateTo == Today &&
                q.Language == "en" && !q.GenerateNarrative),
            Arg.Any<CancellationToken>());
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
