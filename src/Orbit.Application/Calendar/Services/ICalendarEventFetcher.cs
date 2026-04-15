using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar.Services;

public interface ICalendarEventFetcher
{
    /// <summary>
    /// Fetches Google Calendar events from the user's primary calendar for the next 60
    /// days and maps them into <see cref="CalendarEventItem"/> instances. The concrete
    /// implementation lives in Infrastructure and owns Google SDK construction, so
    /// Application only passes an OAuth access token.
    /// Throws <see cref="CalendarProviderException"/> on provider errors; the Kind field
    /// tells callers whether to force a reconnect or retry later.
    /// </summary>
    /// <param name="accessToken">Google OAuth 2.0 access token for the calling user.</param>
    /// <param name="updatedMin">If provided, Google returns only events created/modified after this UTC timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<CalendarEventItem>> FetchAsync(
        string accessToken,
        DateTime? updatedMin,
        CancellationToken ct);
}

/// <summary>
/// Classification of calendar-provider failures used by Application to decide whether
/// to force the user to reconnect vs. mark a transient error for retry.
/// </summary>
public enum CalendarFetchErrorKind
{
    Transient,
    ReconnectRequired,
}

/// <summary>
/// Thrown by calendar fetchers when the upstream provider returns an error. Infrastructure
/// classifies the raw provider exception so Application doesn't need to import the vendor SDK.
/// </summary>
public sealed class CalendarProviderException : Exception
{
    public CalendarFetchErrorKind Kind { get; }
    public string? RawErrorCode { get; }

    public CalendarProviderException(CalendarFetchErrorKind kind, string? rawErrorCode, string message, Exception inner)
        : base(message, inner)
    {
        Kind = kind;
        RawErrorCode = rawErrorCode;
    }
}
