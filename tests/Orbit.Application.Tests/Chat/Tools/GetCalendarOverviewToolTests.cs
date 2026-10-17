using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class GetCalendarOverviewToolTests
{
    [Fact]
    public async Task SyncGateFailure_KeepsEventsAndOmitsSyncRow()
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        var item = new CalendarEventItem("1", "Appointment", null, "2026-09-25", null, null,
            false, null, []);
        mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarEventItem> { item }));
        mediator.Send(Arg.Any<GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<CalendarAutoSyncStateResponse>("gated"));
        mediator.Send(Arg.Any<GetCalendarSyncSuggestionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<List<CalendarSyncSuggestionItem>>("gated"));

        var result = await new GetCalendarOverviewTool(mediator).ExecuteAsync(
            JsonDocument.Parse("{}").RootElement, userId, CancellationToken.None);

        result.Success.Should().BeTrue();
        var payload = result.Payload.Should().BeOfType<CalendarOverviewPayload>().Subject;
        payload.Events.Should().ContainSingle().Which.Should().Be(item);
        payload.AutoSyncState.Should().BeNull();
    }
}
