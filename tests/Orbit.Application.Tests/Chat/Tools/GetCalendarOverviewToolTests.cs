using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public class GetCalendarOverviewToolTests
{
    [Theory]
    [MemberData(nameof(OptionalParameterCases.Fields), typeof(GetCalendarOverviewTool), "", MemberType = typeof(OptionalParameterCases))]
    public async Task SyncGateFailure_KeepsEventsAndOmitsSyncRow(string field)
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

        mediator.Send(Arg.Any<GetUserCalendarsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<List<UserCalendarItem>>("gated"));

        var result = await new GetCalendarOverviewTool(mediator).ExecuteAsync(
            JsonDocument.Parse("{}").RootElement, userId, CancellationToken.None);

        result.Success.Should().BeTrue();
        var payload = result.Payload.Should().BeOfType<CalendarOverviewPayload>().Subject;
        payload.Events.Should().ContainSingle().Which.Should().Be(item);
        payload.AutoSyncState.Should().BeNull();
        var explicitNull = await new GetCalendarOverviewTool(mediator).ExecuteAsync(JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { [field] = null }), userId, default);
        explicitNull.Should().BeEquivalentTo(result);
    }

    [Theory]
    [InlineData("2026-11-06T15:00:00", "2026-11-06T15:00:00.0000000Z")]
    [InlineData("2026-11-06", "2026-11-06T00:00:00.0000000Z")]
    [InlineData("2026-11-06T15:00:00-03:00", "2026-11-06T18:00:00.0000000Z")]
    [InlineData("2026-11-06T15:00:00.0000000Z", "2026-11-06T15:00:00.0000000Z")]
    [InlineData("soon", "2026-04-01T03:00:00.0000000Z")]
    public async Task ReadScopeCarriesExactlyTheWindowTheToolReads(string sent, string expected)
    {
        var mediator = Substitute.For<IMediator>();
        var userId = Guid.NewGuid();
        CalendarReadWindow? read = null;
        mediator.Send(Arg.Do<GetCalendarEventsQuery>(query => read = query.Window), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarEventItem>()));
        mediator.Send(Arg.Any<GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<CalendarAutoSyncStateResponse>("gated"));
        mediator.Send(Arg.Any<GetCalendarSyncSuggestionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<List<CalendarSyncSuggestionItem>>("gated"));
        mediator.Send(Arg.Any<GetUserCalendarsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<List<UserCalendarItem>>("gated"));
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 1));
        dates.GetUserTimeZoneAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"));
        var blocks = new ReadBlockFactory(dates, Substitute.For<IReadQueryTokenService>(), Substitute.For<IGenericRepository<User>>());

        var scope = await blocks.ResolveAsync("get_calendar_overview", JsonSerializer.SerializeToElement(
            new { date_from = "2026-04-01", date_to = "2026-11-10", window_from_utc = sent }), userId, default);
        (await new GetCalendarOverviewTool(mediator).ExecuteAsync(scope, userId, default)).Success.Should().BeTrue();

        read.Should().NotBeNull();
        read!.FromAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        read.FromAtUtc.ToString("O", CultureInfo.InvariantCulture).Should().Be(expected);
        scope.GetProperty("window_from_utc").GetString().Should().Be(expected);
        scope.GetProperty("window_to_utc").GetString().Should().Be(read.ToAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }
}
