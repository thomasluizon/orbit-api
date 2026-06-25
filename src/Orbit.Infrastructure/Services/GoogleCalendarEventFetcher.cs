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
        var seenRecurringMasterIds = new HashSet<string>(StringComparer.Ordinal);
        var masterRRuleCache = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var ev in events)
        {
            if (string.IsNullOrWhiteSpace(ev.Summary)) continue;
            if (string.Equals(ev.Status, "cancelled", StringComparison.OrdinalIgnoreCase)) continue;
            if (ev.RecurringEventId is not null && !seenRecurringMasterIds.Add(ev.RecurringEventId)) continue;

            var rrule = await ResolveRRule(accessToken, calendarId, ev, masterRRuleCache, ct);
            items.Add(MapEvent(ev, calendarId, calendarName, rrule));
        }

        return items;
    }

    private static CalendarEventItem MapEvent(Event ev, string calendarId, string calendarName, string? rrule)
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
            rrule,
            BuildReminders(ev, startTime),
            ResolveStartUtc(ev.Start),
            calendarId,
            calendarName);
    }

    private async Task<string?> ResolveRRule(
        string accessToken,
        string calendarId,
        Event ev,
        Dictionary<string, string?> masterRRuleCache,
        CancellationToken ct)
    {
        if (ev.Recurrence is not null)
            return ev.Recurrence.FirstOrDefault(r => r.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));

        if (ev.RecurringEventId is null)
            return null;

        if (masterRRuleCache.TryGetValue(ev.RecurringEventId, out var cached))
            return cached;

        string? rrule;
        try
        {
            var master = await api.GetEventAsync(accessToken, calendarId, ev.RecurringEventId, ct);
            rrule = master.Recurrence?.FirstOrDefault(r => r.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFetchMasterRruleFailed(logger, ex, ev.RecurringEventId);
            rrule = null;
        }

        masterRRuleCache[ev.RecurringEventId] = rrule;
        return rrule;
    }

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

    private static DateTime? ResolveStartUtc(EventDateTime? start)
    {
        if (start is null)
            return null;

        if (start.DateTimeDateTimeOffset is { } dto)
            return dto.UtcDateTime;

        if (!string.IsNullOrWhiteSpace(start.Date)
            && DateOnly.TryParse(start.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
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
