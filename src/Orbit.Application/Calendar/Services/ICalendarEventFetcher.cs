using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar.Services;

public interface ICalendarEventFetcher
{
    /// <summary>
    /// Fetches Google Calendar events for the next 60 days across the user's calendars and maps
    /// them into <see cref="CalendarEventItem"/> instances tagged with their source calendar.
    /// When <paramref name="selectedCalendarIds"/> is null the user's owned, non-deleted,
    /// non-hidden calendars are used; when non-null only calendars whose id is in that set are
    /// fetched (still skipping deleted/hidden/inaccessible ones). A single calendar that fails
    /// (e.g. transient or permission error) is logged and skipped rather than failing the whole
    /// fetch. The concrete implementation lives in Infrastructure and owns Google SDK construction,
    /// so Application only passes an OAuth access token.
    /// Throws <see cref="CalendarProviderException"/> when the initial calendar-list call fails;
    /// the Kind field tells callers whether to force a reconnect or retry later.
    /// </summary>
    /// <param name="accessToken">Google OAuth 2.0 access token for the calling user.</param>
    /// <param name="selectedCalendarIds">Explicit calendar-id allow-list, or null to use all owned calendars.</param>
    /// <param name="updatedMin">If provided, Google returns only events created/modified after this UTC timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<CalendarEventItem>> FetchAsync(
        string accessToken,
        IReadOnlyCollection<string>? selectedCalendarIds,
        DateTime? updatedMin,
        CancellationToken ct);

    /// <summary>
    /// Lists every non-deleted, non-hidden calendar on the user's calendar list as
    /// <see cref="CalendarListItem"/> instances for a settings picker. Throws
    /// <see cref="CalendarProviderException"/> on provider errors.
    /// </summary>
    /// <param name="accessToken">Google OAuth 2.0 access token for the calling user.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<CalendarListItem>> ListCalendarsAsync(string accessToken, CancellationToken ct);
}

/// <summary>
/// A single calendar from the user's Google calendar list, surfaced to the settings picker.
/// <see cref="IsDefaultOwned"/> reflects the owner/!deleted/!hidden rule used to build the
/// default sync set when the user has no explicit selection.
/// </summary>
public record CalendarListItem(
    string Id,
    string Name,
    string AccessRole,
    bool Primary,
    string? BackgroundColor,
    bool IsDefaultOwned);

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
