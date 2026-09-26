using Google.Apis.Calendar.v3.Data;

namespace Orbit.Infrastructure.Services.Calendar;

internal interface IGoogleCalendarApi
{
    /// <summary>Lists every <see cref="CalendarListEntry"/> on the user's calendar list, following page tokens.</summary>
    Task<IReadOnlyList<CalendarListEntry>> ListCalendarsAsync(string accessToken, CancellationToken ct);

    /// <summary>
    /// Lists events for a single calendar with the fixed forward window (SingleEvents, TimeMin=now,
    /// TimeMax=now+60d, OrderBy=StartTime), following page tokens. <paramref name="updatedMin"/> narrows
    /// the result to events changed after that UTC instant when provided.
    /// </summary>
    Task<IReadOnlyList<Event>> ListEventsAsync(
        string accessToken, string calendarId, DateTime? updatedMin, CancellationToken ct);

    /// <summary>Fetches a single event (used to resolve a recurring master's RRULE).</summary>
    Task<Event> GetEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct);
}
