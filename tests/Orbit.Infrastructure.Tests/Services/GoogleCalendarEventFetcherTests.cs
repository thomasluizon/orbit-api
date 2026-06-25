using FluentAssertions;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Calendar.Services;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Calendar;

namespace Orbit.Infrastructure.Tests.Services;

public class GoogleCalendarEventFetcherTests
{
    private readonly IGoogleCalendarApi _api = Substitute.For<IGoogleCalendarApi>();
    private readonly ILogger<GoogleCalendarEventFetcher> _logger = Substitute.For<ILogger<GoogleCalendarEventFetcher>>();
    private readonly GoogleCalendarEventFetcher _fetcher;

    private const string Token = "access-token";

    public GoogleCalendarEventFetcherTests()
    {
        _fetcher = new GoogleCalendarEventFetcher(_api, _logger);
        _api.ListEventsAsync(Token, Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Event>());
    }

    private static CalendarListEntry Calendar(
        string id, string accessRole, string? summary = null,
        bool? deleted = null, bool? hidden = null, bool? primary = null,
        string? summaryOverride = null, string? backgroundColor = null)
        => new()
        {
            Id = id,
            AccessRole = accessRole,
            Summary = summary ?? id,
            SummaryOverride = summaryOverride,
            Deleted = deleted,
            Hidden = hidden,
            Primary = primary,
            BackgroundColor = backgroundColor
        };

    private static Event TimedEvent(string id, string summary, string? recurringEventId = null)
        => new()
        {
            Id = id,
            Summary = summary,
            RecurringEventId = recurringEventId,
            Start = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero) },
            End = new EventDateTime { DateTimeDateTimeOffset = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero) }
        };

    private void StubCalendars(params CalendarListEntry[] calendars)
        => _api.ListCalendarsAsync(Token, Arg.Any<CancellationToken>())
            .Returns(calendars.ToList());

    private void StubEvents(string calendarId, params Event[] events)
        => _api.ListEventsAsync(Token, calendarId, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(events.ToList());

    [Fact]
    public async Task FetchAsync_NullSelection_IncludesOwnedExcludesReader()
    {
        StubCalendars(
            Calendar("owned", "owner", summary: "Rotina"),
            Calendar("holidays", "reader", summary: "Holidays"));
        StubEvents("owned", TimedEvent("e1", "Workout"));

        var result = await _fetcher.FetchAsync(Token, null, null, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].CalendarId.Should().Be("owned");
        result[0].CalendarName.Should().Be("Rotina");
        await _api.DidNotReceive().ListEventsAsync(Token, "holidays", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAsync_NullSelection_ExcludesDeletedAndHiddenOwned()
    {
        StubCalendars(
            Calendar("deleted", "owner", deleted: true),
            Calendar("hidden", "owner", hidden: true),
            Calendar("live", "owner"));
        StubEvents("live", TimedEvent("e1", "Live event"));

        var result = await _fetcher.FetchAsync(Token, null, null, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].CalendarId.Should().Be("live");
        await _api.DidNotReceive().ListEventsAsync(Token, "deleted", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().ListEventsAsync(Token, "hidden", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAsync_MergesEventsAcrossOwnedCalendarsWithTagging()
    {
        StubCalendars(
            Calendar("a", "owner", summary: "Calendar A"),
            Calendar("b", "owner", summary: "B raw", summaryOverride: "B Override"));
        StubEvents("a", TimedEvent("a1", "From A"));
        StubEvents("b", TimedEvent("b1", "From B"));

        var result = await _fetcher.FetchAsync(Token, null, null, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().ContainSingle(i => i.CalendarId == "a" && i.CalendarName == "Calendar A");
        result.Should().ContainSingle(i => i.CalendarId == "b" && i.CalendarName == "B Override");
    }

    [Fact]
    public async Task FetchAsync_ExplicitSelection_FetchesOnlyChosenCalendars()
    {
        StubCalendars(
            Calendar("a", "owner"),
            Calendar("b", "owner"),
            Calendar("shared", "reader"));
        StubEvents("shared", TimedEvent("s1", "Shared event"));

        var result = await _fetcher.FetchAsync(Token, new[] { "shared" }, null, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].CalendarId.Should().Be("shared");
        await _api.Received(1).ListEventsAsync(Token, "shared", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().ListEventsAsync(Token, "a", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAsync_ExplicitSelection_StillSkipsDeletedAndHidden()
    {
        StubCalendars(Calendar("chosen", "owner", deleted: true));

        var result = await _fetcher.FetchAsync(Token, new[] { "chosen" }, null, CancellationToken.None);

        result.Should().BeEmpty();
        await _api.DidNotReceive().ListEventsAsync(Token, "chosen", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAsync_RecurringMasterDedup_IsPerCalendar()
    {
        StubCalendars(Calendar("a", "owner"), Calendar("b", "owner"));
        StubEvents("a",
            TimedEvent("a-inst-1", "Standup", recurringEventId: "master"),
            TimedEvent("a-inst-2", "Standup", recurringEventId: "master"));
        StubEvents("b", TimedEvent("b-inst-1", "Standup", recurringEventId: "master"));

        var result = await _fetcher.FetchAsync(Token, null, null, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(i => i.Id == "master");
        result.Select(i => i.CalendarId).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public async Task FetchAsync_FailingCalendar_IsSkippedNotFatal()
    {
        StubCalendars(Calendar("good", "owner"), Calendar("bad", "owner"));
        StubEvents("good", TimedEvent("g1", "Good event"));
        _api.ListEventsAsync(Token, "bad", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Event>>>(_ => throw new InvalidOperationException("boom"));

        var result = await _fetcher.FetchAsync(Token, null, null, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].CalendarId.Should().Be("good");
    }

    [Fact]
    public async Task FetchAsync_SkipsCancelledAndUntitledEvents()
    {
        StubCalendars(Calendar("a", "owner"));
        var cancelled = TimedEvent("c1", "Cancelled");
        cancelled.Status = "cancelled";
        var untitled = TimedEvent("u1", "   ");
        StubEvents("a", cancelled, untitled, TimedEvent("ok", "Kept"));

        var result = await _fetcher.FetchAsync(Token, null, null, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Id.Should().Be("ok");
    }

    [Fact]
    public async Task FetchAsync_ListCalendarsAuthError_ThrowsReconnectRequired()
    {
        _api.ListCalendarsAsync(Token, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CalendarListEntry>>>(_ => throw new Google.GoogleApiException("calendar", "insufficient authentication scopes")
            {
                HttpStatusCode = System.Net.HttpStatusCode.Forbidden
            });

        var act = async () => await _fetcher.FetchAsync(Token, null, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<CalendarProviderException>();
        ex.Which.Kind.Should().Be(CalendarFetchErrorKind.ReconnectRequired);
    }

    [Fact]
    public async Task ListCalendarsAsync_MapsEntriesAndComputesDefaultOwned()
    {
        StubCalendars(
            Calendar("owned", "owner", summary: "Rotina", primary: true, backgroundColor: "#fff"),
            Calendar("shared", "reader", summary: "Team"),
            Calendar("hiddenOwned", "owner", hidden: true));

        var result = await _fetcher.ListCalendarsAsync(Token, CancellationToken.None);

        result.Should().HaveCount(2);
        var owned = result.Single(c => c.Id == "owned");
        owned.Name.Should().Be("Rotina");
        owned.Primary.Should().BeTrue();
        owned.BackgroundColor.Should().Be("#fff");
        owned.IsDefaultOwned.Should().BeTrue();
        result.Single(c => c.Id == "shared").IsDefaultOwned.Should().BeFalse();
    }
}
