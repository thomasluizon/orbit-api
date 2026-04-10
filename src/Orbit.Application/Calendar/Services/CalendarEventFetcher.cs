using Google.Apis.Calendar.v3;
using Microsoft.Extensions.Logging;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Common;

namespace Orbit.Application.Calendar.Services;

public partial class CalendarEventFetcher(ILogger<CalendarEventFetcher> logger) : ICalendarEventFetcher
{
    public async Task<List<CalendarEventItem>> FetchAsync(
        CalendarService service,
        DateTime? updatedMin,
        CancellationToken ct)
    {
        var events = await ExecuteList(service, updatedMin, ct);

        var items = new List<CalendarEventItem>();
        var seenRecurringMasterIds = new HashSet<string>();
        var masterRRuleCache = new Dictionary<string, string?>();

        foreach (var ev in events.Items ?? [])
        {
            if (string.IsNullOrWhiteSpace(ev.Summary)) continue;

            // Skip cancelled events that leak in when updatedMin is set
            if (string.Equals(ev.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                continue;

            // Collapse all instances of a recurring series to a single row (the master)
            var masterId = ev.RecurringEventId ?? ev.Id;
            if (ev.RecurringEventId is not null && !seenRecurringMasterIds.Add(ev.RecurringEventId))
                continue;

            var evTitle = ev.Summary.Trim();
            var startDate = ev.Start?.Date ?? ev.Start?.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd");
            var startTime = ev.Start?.DateTimeDateTimeOffset?.ToString("HH:mm");
            var endTime = ev.End?.DateTimeDateTimeOffset?.ToString("HH:mm");
            var isRecurring = ev.RecurringEventId is not null
                || (ev.Recurrence is not null && ev.Recurrence.Count > 0);

            var rrule = await ResolveRRule(ev, service, masterRRuleCache, ct);
            var reminders = BuildReminders(ev, startTime);

            items.Add(new CalendarEventItem(
                masterId, evTitle, ev.Description,
                startDate, startTime, endTime,
                isRecurring, rrule, reminders));
        }

        return items;
    }

    private static async Task<Google.Apis.Calendar.v3.Data.Events> ExecuteList(
        CalendarService service, DateTime? updatedMin, CancellationToken ct)
    {
        var listRequest = service.Events.List("primary");
        listRequest.SingleEvents = true;
        listRequest.TimeMinDateTimeOffset = DateTimeOffset.UtcNow;
        listRequest.TimeMaxDateTimeOffset = DateTimeOffset.UtcNow.AddDays(60);
        listRequest.MaxResults = 250;
        listRequest.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        if (updatedMin.HasValue)
        {
            listRequest.UpdatedMinDateTimeOffset = new DateTimeOffset(
                DateTime.SpecifyKind(updatedMin.Value, DateTimeKind.Utc));
        }

        return await listRequest.ExecuteAsync(ct);
    }

    private async Task<string?> ResolveRRule(
        Google.Apis.Calendar.v3.Data.Event ev,
        CalendarService service,
        Dictionary<string, string?> masterRRuleCache,
        CancellationToken cancellationToken)
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
            var master = await service.Events.Get("primary", ev.RecurringEventId).ExecuteAsync(cancellationToken);
            rrule = master.Recurrence?.FirstOrDefault(r => r.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            LogFetchMasterRruleFailed(logger, ex, ev.RecurringEventId);
            rrule = null;
        }

        masterRRuleCache[ev.RecurringEventId] = rrule;
        return rrule;
    }

    internal static List<int> BuildReminders(
        Google.Apis.Calendar.v3.Data.Event ev, string? startTime)
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to fetch master event RRULE for recurring event {EventId}")]
    private static partial void LogFetchMasterRruleFailed(ILogger logger, Exception ex, string? eventId);
}
