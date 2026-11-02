using System.Net;
using FluentAssertions;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Json;
using Orbit.Infrastructure.Services.Calendar;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class GoogleCalendarApiTests
{
    [Fact]
    public async Task ListCalendars_TwoPages_ReturnsAllInOrder()
    {
        var handler = new PagesHandler(
            new CalendarList { Items = [new CalendarListEntry { Id = "first" }], NextPageToken = "next" },
            new CalendarList { Items = [new CalendarListEntry { Id = "second" }] });
        var result = await new GoogleCalendarApi(TimeSpan.FromSeconds(10), handler).ListCalendarsAsync("token", default);
        result.Select(x => x.Id).Should().Equal("first", "second");
        handler.Urls[1].Query.Should().Contain("pageToken=next");
    }

    [Fact]
    public async Task ListEvents_TwoPages_ReturnsAllInOrder()
    {
        var handler = new PagesHandler(
            new Google.Apis.Calendar.v3.Data.Events { Items = [new Event { Id = "first" }], NextPageToken = "next" },
            new Google.Apis.Calendar.v3.Data.Events { Items = [new Event { Id = "second" }] });
        var result = await new GoogleCalendarApi(TimeSpan.FromSeconds(10), handler).ListEventsAsync("token", "calendar", null, default);
        result.Select(x => x.Id).Should().Equal("first", "second");
        handler.Urls[1].Query.Should().Contain("pageToken=next");
    }

    [Fact]
    public async Task GetEvent_UsesCalendarAndEventIds()
    {
        var handler = new PagesHandler(new Event { Id = "event" });
        await new GoogleCalendarApi(TimeSpan.FromSeconds(10), handler).GetEventAsync("token", "calendar@example.com", "event", default);
        Uri.UnescapeDataString(handler.Urls.Single().AbsolutePath).Should().EndWith("/calendars/calendar@example.com/events/event");
    }

    private sealed class PagesHandler(params object[] pages) : HttpMessageHandler
    {
        public List<Uri> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var page = pages[Urls.Count];
            Urls.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(NewtonsoftJsonSerializer.Instance.Serialize(page))
            });
        }
    }
}
