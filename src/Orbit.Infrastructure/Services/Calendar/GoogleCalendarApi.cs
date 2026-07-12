using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

namespace Orbit.Infrastructure.Services.Calendar;

/// <summary>
/// Production <see cref="IGoogleCalendarApi"/> backed by the Google Calendar v3 SDK. Owns
/// SDK construction (so Application never sees vendor types) and drains every list endpoint's
/// pagination via its page-token loop. Kept deliberately logic-free: filtering, dedup, and
/// mapping live in <see cref="GoogleCalendarEventFetcher"/> so they stay unit-testable.
/// </summary>
internal sealed class GoogleCalendarApi(TimeSpan httpTimeout) : IGoogleCalendarApi
{
    private const int EventsPageSize = 2500;

    public async Task<IReadOnlyList<CalendarListEntry>> ListCalendarsAsync(string accessToken, CancellationToken ct)
    {
        using var service = CreateCalendarService(accessToken);

        var entries = new List<CalendarListEntry>();
        string? pageToken = null;
        do
        {
            var request = service.CalendarList.List();
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(ct);
            if (response.Items is { Count: > 0 })
                entries.AddRange(response.Items);
            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return entries;
    }

    public async Task<IReadOnlyList<Event>> ListEventsAsync(
        string accessToken, string calendarId, DateTime? updatedMin, CancellationToken ct)
    {
        using var service = CreateCalendarService(accessToken);

        var events = new List<Event>();
        string? pageToken = null;
        do
        {
            var request = BuildEventsRequest(service, calendarId, updatedMin);
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(ct);
            if (response.Items is { Count: > 0 })
                events.AddRange(response.Items);
            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return events;
    }

    public async Task<Event> GetEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct)
    {
        using var service = CreateCalendarService(accessToken);
        return await service.Events.Get(calendarId, eventId).ExecuteAsync(ct);
    }

    private static EventsResource.ListRequest BuildEventsRequest(
        CalendarService service, string calendarId, DateTime? updatedMin)
    {
        var request = service.Events.List(calendarId);
        request.SingleEvents = true;
        request.TimeMinDateTimeOffset = DateTimeOffset.UtcNow;
        request.TimeMaxDateTimeOffset = DateTimeOffset.UtcNow.AddDays(60);
        request.MaxResults = EventsPageSize;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        if (updatedMin.HasValue)
        {
            request.UpdatedMinDateTimeOffset = new DateTimeOffset(
                DateTime.SpecifyKind(updatedMin.Value, DateTimeKind.Utc));
        }

        return request;
    }

    private CalendarService CreateCalendarService(string accessToken)
    {
        var credential = GoogleCredential.FromAccessToken(accessToken);
        var service = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Orbit"
        });
        service.HttpClient.Timeout = httpTimeout;
        return service;
    }
}
