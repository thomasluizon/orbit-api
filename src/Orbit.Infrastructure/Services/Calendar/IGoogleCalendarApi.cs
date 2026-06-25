using Google.Apis.Calendar.v3.Data;

namespace Orbit.Infrastructure.Services.Calendar;

/// <summary>
/// Thin testable seam over the Google Calendar SDK. Production wraps the real
/// <c>CalendarService</c>; tests substitute it to exercise the owned-calendar filter,
/// per-calendar aggregation, pagination, and dedup logic in <see cref="GoogleCalendarEventFetcher"/>
/// without the vendor SDK. Each method already drains the provider's pagination so callers
/// receive the full result set.
/// </summary>
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
