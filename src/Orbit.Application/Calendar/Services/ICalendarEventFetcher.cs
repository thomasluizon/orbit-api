using Google.Apis.Calendar.v3;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar.Services;

public interface ICalendarEventFetcher
{
    /// <summary>
    /// Fetches Google Calendar events from the primary calendar for the next 60 days
    /// and maps them into <see cref="CalendarEventItem"/> instances.
    /// The caller is responsible for providing an authenticated <see cref="CalendarService"/>
    /// and for filtering out already-imported events.
    /// </summary>
    /// <param name="service">An authenticated Google Calendar service.</param>
    /// <param name="updatedMin">When provided, Google only returns events created/modified after this timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<CalendarEventItem>> FetchAsync(
        CalendarService service,
        DateTime? updatedMin,
        CancellationToken ct);
}
