using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat.Tools;

public class GetHabitMetricsToolTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly GetHabitMetricsTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid HabitId = Guid.NewGuid();

    public GetHabitMetricsToolTests()
    {
        _tool = new GetHabitMetricsTool(_mediator);
    }

    [Fact]
    public async Task Success_ReturnsPayload()
    {
        var metrics = new HabitMetrics(
            CurrentStreak: 5,
            LongestStreak: 12,
            WeeklyCompletionRate: 80m,
            MonthlyCompletionRate: 70m,
            TotalCompletions: 42,
            LastCompletedDate: new DateOnly(2026, 6, 3));
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(metrics));

        var result = await Execute($$$"""{"habit_id": "{{{HabitId}}}"}""");

        result.Success.Should().BeTrue();
        result.Payload.Should().Be(metrics);
        await _mediator.Received(1).Send(
            Arg.Is<GetHabitMetricsQuery>(q => q.UserId == UserId && q.HabitId == HabitId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidGuid_ReturnsErrorWithoutSendingQuery()
    {
        var result = await Execute("""{"habit_id": "not-a-guid"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id must be a valid GUID");
        await _mediator.DidNotReceive().Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitNotFound_PropagatesError()
    {
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitMetrics>(ErrorMessages.HabitNotFound, ErrorCodes.HabitNotFound));

        var result = await Execute($$$"""{"habit_id": "{{{HabitId}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be(ErrorMessages.HabitNotFound);
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
