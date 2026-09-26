using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar.Services;

public interface ICalendarEventFetcher
{
    Task<List<CalendarEventItem>> FetchAsync(
        string accessToken,
        IReadOnlyCollection<string>? selectedCalendarIds,
        DateTime? updatedMin,
        CancellationToken ct);

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
