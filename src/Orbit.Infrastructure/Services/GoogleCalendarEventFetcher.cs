using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services.Calendar;

namespace Orbit.Infrastructure.Services;

internal sealed partial class GoogleCalendarEventFetcher(
    IGoogleCalendarApi api,
    ILogger<GoogleCalendarEventFetcher> logger, IConfiguration? configuration = null) : ICalendarEventFetcher, IDisposable
{
    private readonly SemaphoreSlim _requests = new(Math.Clamp(configuration?.GetValue("CalendarSync:FetchParallelism", 4) ?? 4, 1, 8));

    public void Dispose() => _requests.Dispose();

    private async Task<T> RequestAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        await _requests.WaitAsync(ct);
        try { return await operation(); }
        finally { _requests.Release(); }
    }

    public async Task<List<CalendarEventItem>> FetchAsync(
        string accessToken,
        IReadOnlyCollection<string>? selectedCalendarIds,
        DateTime? updatedMin,
        CancellationToken ct, CalendarReadWindow? window = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration?.GetValue("CalendarSync:FetchDeadlineSeconds", 90) ?? 90, 1, 300)));
        ct = deadline.Token;
        var calendars = await ListCalendarEntries(accessToken, ct);
        var targets = SelectTargetCalendars(calendars, selectedCalendarIds);

        var batches = new List<CalendarEventItem>[targets.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, targets.Count), new ParallelOptions
        { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
        {
            batches[index] = await FetchCalendarEvents(accessToken, targets[index], updatedMin, token, window);
        });
        return batches.SelectMany(batch => batch).ToList();
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
            return await RequestAsync(() => api.ListCalendarsAsync(accessToken, ct), ct);
        }
        catch (Google.GoogleApiException ex)
        {
            throw ToProviderException(ex);
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
        string accessToken, CalendarListEntry calendar, DateTime? updatedMin, CancellationToken ct, CalendarReadWindow? window)
    {
        var calendarName = ResolveCalendarName(calendar);
        IReadOnlyList<Event> events;
        try
        {
            events = await RequestAsync(() => api.ListEventsAsync(accessToken, calendar.Id, updatedMin, ct, window), ct);
        }
        catch (Google.GoogleApiException ex) when (ClassifyGoogleError(ex) != CalendarFetchErrorKind.Transient)
        {
            throw ToProviderException(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (window is not null)
                throw new CalendarProviderException(CalendarFetchErrorKind.Transient, null,
                    "The exact calendar window could not be read completely.", ex);
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
        var masterRecurrenceCache = new MasterCache();
        var expandedOccurrences = events
            .Where(ev => ev.RecurringEventId is not null
                && !string.IsNullOrWhiteSpace(ev.Summary)
                && !string.Equals(ev.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            .GroupBy(ev => ev.RecurringEventId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<DateTime>)group
                    .Select(ev => ev.OriginalStartTime?.DateTimeDateTimeOffset?.UtcDateTime
                        ?? ev.Start?.DateTimeDateTimeOffset?.UtcDateTime)
                    .Where(start => start.HasValue)
                    .Select(start => start!.Value)
                    .ToList(),
                StringComparer.Ordinal);

        var masters = events.Where(ev => ev.RecurringEventId != null && ev.Recurrence == null
                && !string.IsNullOrWhiteSpace(ev.Summary) && ev.Status != "cancelled")
            .DistinctBy(ev => ev.RecurringEventId).ToArray();
        await Parallel.ForEachAsync(masters, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (ev, token) => { await ResolveRecurrence(accessToken, calendarId, ev, masterRecurrenceCache, token); });
        if (masterRecurrenceCache.Failures > 0)
            LogFetchMastersFailed(logger, calendarId, masterRecurrenceCache.Failures);

        foreach (var ev in events)
        {
            if (string.IsNullOrWhiteSpace(ev.Summary)) continue;
            if (string.Equals(ev.Status, "cancelled", StringComparison.OrdinalIgnoreCase)) continue;
            if (ev.RecurringEventId is { } masterId && !seenRecurringMasters.Add(masterId)) continue;

            var recurrence = await ResolveRecurrence(accessToken, calendarId, ev, masterRecurrenceCache, ct);
            var item = MapEvent(ev, calendarId, calendarName, recurrence);
            if (ev.RecurringEventId is { } recurringId
                && expandedOccurrences.TryGetValue(recurringId, out var occurrences))
                item = item with { ExpandedOccurrencesUtc = occurrences };
            else if (item.IsRecurring && item.RecurrenceStartUtc is { } recurrenceStart)
                item = item with { ExpandedOccurrencesUtc = [recurrenceStart] };
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// The recurrence facts that live on the master event rather than on an expanded instance: the
    /// rule itself, and the IANA zone the master expands that rule in.
    /// </summary>
    private sealed class MasterCache
    {
        internal readonly ConcurrentDictionary<string, MasterRecurrence> Rows = new(StringComparer.Ordinal);
        internal int Failures;
    }

    private readonly record struct MasterRecurrence(string? Rule, string? TimeZone);

    private static CalendarEventItem MapEvent(
        Event ev, string calendarId, string calendarName, MasterRecurrence recurrence)
    {
        var startTime = ev.Start?.DateTimeDateTimeOffset?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var isRecurring = ev.RecurringEventId is not null
            || (ev.Recurrence is not null && ev.Recurrence.Count > 0);

        return new CalendarEventItem(
            ev.RecurringEventId ?? ev.Id,
            ev.Summary.Trim(),
            ev.Description,
            ev.Start?.Date ?? ev.Start?.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            startTime,
            ev.End?.DateTimeDateTimeOffset?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            isRecurring,
            recurrence.Rule,
            BuildReminders(ev, startTime),
            ResolveStartUtc(ev.Start),
            calendarId,
            calendarName,
            ev.End?.DateTimeDateTimeOffset?.UtcDateTime,
            isRecurring && startTime is not null && !string.IsNullOrWhiteSpace(recurrence.TimeZone)
                ? recurrence.TimeZone
                : null)
        {
            SourceTimeZone = recurrence.TimeZone,
            RecurrenceStartUtc = !isRecurring
                ? null
                : ev.RecurringEventId is not null
                    ? ev.OriginalStartTime?.DateTimeDateTimeOffset?.UtcDateTime
                    : ev.Start?.DateTimeDateTimeOffset?.UtcDateTime
        };
    }

    private async Task<MasterRecurrence> ResolveRecurrence(
        string accessToken,
        string calendarId,
        Event ev,
        MasterCache masterRecurrenceCache,
        CancellationToken ct)
    {
        if (ev.Recurrence is not null)
            return ReadRecurrence(ev);

        if (ev.RecurringEventId is null)
            return default;

        if (masterRecurrenceCache.Rows.TryGetValue(ev.RecurringEventId, out var cached))
            return cached;

        MasterRecurrence recurrence;
        try
        {
            var master = await RequestAsync(() => api.GetEventAsync(accessToken, calendarId, ev.RecurringEventId, ct), ct);
            recurrence = ReadRecurrence(master);
        }
        catch (Google.GoogleApiException ex) when (ClassifyGoogleError(ex) != CalendarFetchErrorKind.Transient)
        {
            throw ToProviderException(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref masterRecurrenceCache.Failures);
            recurrence = default;
        }

        masterRecurrenceCache.Rows[ev.RecurringEventId] = recurrence;
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

    private static CalendarProviderException ToProviderException(Google.GoogleApiException ex)
    {
        var kind = ClassifyGoogleError(ex);
        var rawCode = kind == CalendarFetchErrorKind.MissingScope
            ? "insufficientPermissions"
            : NormalizeGoogleApiErrorCode(ex);
        return new CalendarProviderException(kind, rawCode, $"Google Calendar API error: {rawCode}", ex);
    }

    private static CalendarFetchErrorKind ClassifyGoogleError(Google.GoogleApiException ex)
    {
        var errorText = NormalizeGoogleApiErrorCode(ex);
        if (ex.Error?.Errors?.Any(error => string.Equals(
                error.Reason, "insufficientPermissions", StringComparison.OrdinalIgnoreCase)) == true
            || errorText.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase))
            return CalendarFetchErrorKind.MissingScope;

        if (ex.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized
            || errorText.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("invalid authentication credentials", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            return CalendarFetchErrorKind.ReconnectRequired;

        return CalendarFetchErrorKind.Transient;
    }

    private static string NormalizeGoogleApiErrorCode(Google.GoogleApiException ex)
    {
        var message = ex.Error?.Message;
        if (!string.IsNullOrWhiteSpace(message))
            return message;
        return ex.Message;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to read {Count} recurring masters for calendar {CalendarId}")]
    private static partial void LogFetchMastersFailed(ILogger logger, string calendarId, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Skipped Google Calendar {CalendarId} after a fetch error")]
    private static partial void LogCalendarFetchSkipped(ILogger logger, Exception ex, string calendarId);
}
