using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services.Calendar;

namespace Orbit.Infrastructure.Services;

/// <summary>
/// Google-Calendar-backed implementation of <see cref="ICalendarEventFetcher"/>. Fans the
/// fetch out across the user's owned (or explicitly selected) calendars, merging the results
/// and tagging each event with its source calendar. Vendor SDK construction is delegated to
/// <see cref="IGoogleCalendarApi"/> so this aggregation/filter/dedup logic stays unit-testable
/// (Clean Architecture: vendor integrations belong in Infrastructure).
/// </summary>
internal sealed partial class GoogleCalendarEventFetcher(
    IGoogleCalendarApi api,
    ILogger<GoogleCalendarEventFetcher> logger) : ICalendarEventFetcher
{
    public async Task<List<CalendarEventItem>> FetchAsync(
        string accessToken,
        IReadOnlyCollection<string>? selectedCalendarIds,
        DateTime? updatedMin,
        CancellationToken ct)
    {
        var calendars = await ListCalendarEntries(accessToken, ct);
        var targets = SelectTargetCalendars(calendars, selectedCalendarIds);

        var items = new List<CalendarEventItem>();
        foreach (var calendar in targets)
        {
            ct.ThrowIfCancellationRequested();
            items.AddRange(await FetchCalendarEvents(accessToken, calendar, updatedMin, ct));
        }

        return items;
    }

    public async Task<List<CalendarListItem>> ListCalendarsAsync(string accessToken, CancellationToken ct)
    {
        var calendars = await ListCalendarEntries(accessToken, ct);
        return calendars
            .Where(c => c.Deleted != true && c.Hidden != true)
            .Select(c => new CalendarListItem(
                c.Id,
                ResolveCalendarName(c),
                c.AccessRole ?? string.Empty,
                c.Primary == true,
                c.BackgroundColor,
                IsDefaultOwned(c)))
            .ToList();
    }

    private async Task<IReadOnlyList<CalendarListEntry>> ListCalendarEntries(string accessToken, CancellationToken ct)
    {
        try
        {
            return await api.ListCalendarsAsync(accessToken, ct);
        }
        catch (Google.GoogleApiException ex)
        {
            var rawCode = NormalizeGoogleApiErrorCode(ex);
            throw new CalendarProviderException(
                ClassifyGoogleError(ex),
                rawCode,
                $"Google Calendar API error: {rawCode}",
                ex);
        }
    }

    private static List<CalendarListEntry> SelectTargetCalendars(
        IReadOnlyList<CalendarListEntry> calendars, IReadOnlyCollection<string>? selectedCalendarIds)
    {
        var accessible = calendars.Where(c => c.Deleted != true && c.Hidden != true);

        if (selectedCalendarIds is null)
            return accessible.Where(IsDefaultOwned).ToList();

        var selected = new HashSet<string>(selectedCalendarIds, StringComparer.Ordinal);
        return accessible.Where(c => selected.Contains(c.Id)).ToList();
    }

    private async Task<List<CalendarEventItem>> FetchCalendarEvents(
        string accessToken, CalendarListEntry calendar, DateTime? updatedMin, CancellationToken ct)
    {
        var calendarName = ResolveCalendarName(calendar);
        IReadOnlyList<Event> events;
        try
        {
            events = await api.ListEventsAsync(accessToken, calendar.Id, updatedMin, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCalendarFetchSkipped(logger, ex, calendar.Id);
            return [];
        }

        return await MapCalendarEvents(accessToken, calendar.Id, calendarName, events, ct);
    }

    private async Task<List<CalendarEventItem>> MapCalendarEvents(
        string accessToken, string calendarId, string calendarName, IReadOnlyList<Event> events, CancellationToken ct)
    {
        var items = new List<CalendarEventItem>();
        var seenRecurringMasters = new HashSet<string>(StringComparer.Ordinal);
        var masterRecurrenceCache = new Dictionary<string, MasterRecurrence>(StringComparer.Ordinal);

        foreach (var ev in events)
        {
            if (string.IsNullOrWhiteSpace(ev.Summary)) continue;
            if (string.Equals(ev.Status, "cancelled", StringComparison.OrdinalIgnoreCase)) continue;
            if (ev.RecurringEventId is { } masterId && !seenRecurringMasters.Add(masterId)) continue;

            var recurrence = await ResolveRecurrence(accessToken, calendarId, ev, masterRecurrenceCache, ct);
            items.Add(MapEvent(ev, calendarId, calendarName, recurrence));
        }

        return items;
    }

    /// <summary>
    /// The recurrence facts that live on the master event rather than on an expanded instance: the
    /// rule itself, and the IANA zone the master expands that rule in.
    /// </summary>
    private readonly record struct MasterRecurrence(string? Rule, string? TimeZone);

    private static CalendarEventItem MapEvent(
        Event ev, string calendarId, string calendarName, MasterRecurrence recurrence)
    {
        var startTime = ev.Start?.DateTimeDateTimeOffset?.ToString("HH:mm");
        var isRecurring = ev.RecurringEventId is not null
            || (ev.Recurrence is not null && ev.Recurrence.Count > 0);

        return new CalendarEventItem(
            ev.RecurringEventId ?? ev.Id,
            ev.Summary.Trim(),
            ev.Description,
            ev.Start?.Date ?? ev.Start?.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd"),
            startTime,
            ev.End?.DateTimeDateTimeOffset?.ToString("HH:mm"),
            isRecurring,
            recurrence.Rule,
            BuildReminders(ev, startTime),
            ResolveStartUtc(ev.Start),
            calendarId,
            calendarName,
            ev.End?.DateTimeDateTimeOffset?.UtcDateTime)
        {
            SourceTimeZone = recurrence.TimeZone
        };
    }

    /// <summary>
    /// Reads the rule and the expansion timezone from whichever event actually owns them. Google
    /// declares <c>EventDateTime.TimeZone</c> required on a recurring event and says the recurrence is
    /// expanded in it, but an expanded instance is not a recurring event, so the value is read from the
    /// master that the list request never returns
    /// (<c>Google.Apis.Calendar.v3.xml</c>, <c>EventDateTime.TimeZone</c>, package 1.75.0.4206).
    /// </summary>
    private async Task<MasterRecurrence> ResolveRecurrence(
        string accessToken,
        string calendarId,
        Event ev,
        Dictionary<string, MasterRecurrence> masterRecurrenceCache,
        CancellationToken ct)
    {
        if (ev.Recurrence is not null)
            return ReadRecurrence(ev);

        if (ev.RecurringEventId is null)
            return default;

        if (masterRecurrenceCache.TryGetValue(ev.RecurringEventId, out var cached))
            return cached;

        MasterRecurrence recurrence;
        try
        {
            var master = await api.GetEventAsync(accessToken, calendarId, ev.RecurringEventId, ct);
            recurrence = ReadRecurrence(master);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFetchMasterRruleFailed(logger, ex, ev.RecurringEventId);
            recurrence = default;
        }

        masterRecurrenceCache[ev.RecurringEventId] = recurrence;
        return recurrence;
    }

    private static MasterRecurrence ReadRecurrence(Event master)
        => new(
            master.Recurrence?.FirstOrDefault(r => r.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)),
            master.Start?.TimeZone);

    internal static List<int> BuildReminders(Event ev, string? startTime)
    {
        var reminders = ev.Reminders?.Overrides?
            .Where(r => r.Minutes.HasValue)
            .Select(r => r.Minutes!.Value)
            .Distinct()
            .ToList() ?? [];

        if (startTime is null)
            return reminders;

        if (reminders.Count == 0)
            reminders.Add(AppConstants.DefaultReminderMinutes);

        if (!reminders.Contains(0))
            reminders.Add(0);

        return reminders;
    }

    private static bool IsDefaultOwned(CalendarListEntry entry) =>
        string.Equals(entry.AccessRole, "owner", StringComparison.OrdinalIgnoreCase)
        && entry.Deleted != true
        && entry.Hidden != true;

    private static string ResolveCalendarName(CalendarListEntry entry) =>
        entry.SummaryOverride ?? entry.Summary ?? string.Empty;

    /// <summary>
    /// The start instant, or midnight UTC of an all-day event's floating start date, which the stored
    /// suggestion row orders and filters on. The matching end carries no such fallback on purpose:
    /// Google's all-day end date is EXCLUSIVE, so reading it the same way would report a one-day
    /// holiday as ending on the following day.
    /// </summary>
    private static DateTime? ResolveStartUtc(EventDateTime? value)
    {
        if (value is null)
            return null;

        if (value.DateTimeDateTimeOffset is { } dto)
            return dto.UtcDateTime;

        if (!string.IsNullOrWhiteSpace(value.Date)
            && DateOnly.TryParse(value.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
        {
            return date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        }

        return null;
    }

    private static CalendarFetchErrorKind ClassifyGoogleError(Google.GoogleApiException ex)
    {
        var errorText = NormalizeGoogleApiErrorCode(ex);
        var isAuthStatus = ex.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;

        if (isAuthStatus
            || errorText.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("invalid authentication credentials", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
        {
            return CalendarFetchErrorKind.ReconnectRequired;
        }
        return CalendarFetchErrorKind.Transient;
    }

    private static string NormalizeGoogleApiErrorCode(Google.GoogleApiException ex)
    {
        var message = ex.Error?.Message;
        if (!string.IsNullOrWhiteSpace(message))
            return message;
        return ex.Message;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to fetch master event RRULE for recurring event {EventId}")]
    private static partial void LogFetchMasterRruleFailed(ILogger logger, Exception ex, string? eventId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Skipped Google Calendar {CalendarId} after a fetch error")]
    private static partial void LogCalendarFetchSkipped(ILogger logger, Exception ex, string calendarId);
}
