using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Queries;

public record CalendarEventItem(
    string Id,
    string Title,
    string? Description,
    string? StartDate,
    string? StartTime,
    string? EndTime,
    bool IsRecurring,
    string? RecurrenceRule,
    List<int> Reminders);

public record GetCalendarEventsQuery(Guid UserId) : IRequest<Result<List<CalendarEventItem>>>;

public partial class GetCalendarEventsQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGoogleTokenService googleTokenService,
    IUnitOfWork unitOfWork,
    ILogger<GetCalendarEventsQueryHandler> logger) : IRequestHandler<GetCalendarEventsQuery, Result<List<CalendarEventItem>>>
{
    public async Task<Result<List<CalendarEventItem>>> Handle(GetCalendarEventsQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<List<CalendarEventItem>>(ErrorMessages.UserNotFound, ErrorCodes.UserNotFound);

        var accessToken = await googleTokenService.GetValidAccessTokenAsync(user, cancellationToken);
        if (accessToken is null)
            return Result.Failure<List<CalendarEventItem>>("Google Calendar not connected. Please sign in with Google first.");

        await unitOfWork.SaveChangesAsync(cancellationToken); // Persist refreshed token

        try
        {
            var service = CreateCalendarService(accessToken);
            var events = await FetchCalendarEvents(service, cancellationToken);
            var existingHabitFilter = await BuildExistingHabitFilter(request.UserId, cancellationToken);

            var items = await MapEventsToItems(events, existingHabitFilter, service, cancellationToken);
            return Result.Success(items);
        }
        catch (Google.GoogleApiException ex)
        {
            LogGoogleCalendarApiError(logger, ex, request.UserId);
            return Result.Failure<List<CalendarEventItem>>("Failed to fetch calendar events. Please try again.");
        }
    }

    private static CalendarService CreateCalendarService(string accessToken)
    {
        var credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromAccessToken(accessToken);
        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Orbit"
        });
    }

    private static async Task<Google.Apis.Calendar.v3.Data.Events> FetchCalendarEvents(
        CalendarService service, CancellationToken cancellationToken)
    {
        var listRequest = service.Events.List("primary");
        listRequest.SingleEvents = true;
        listRequest.TimeMinDateTimeOffset = DateTimeOffset.UtcNow;
        listRequest.TimeMaxDateTimeOffset = DateTimeOffset.UtcNow.AddDays(60);
        listRequest.MaxResults = 250;
        listRequest.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        return await listRequest.ExecuteAsync(cancellationToken);
    }

    /// <summary>
    /// Holds pre-built sets for filtering out already-imported calendar events.
    /// </summary>
    private sealed record ExistingHabitFilter(
        HashSet<string> RecurringHabitTitles,
        HashSet<(string Title, string Date, string Time)> OneTimeHabitKeys);

    private async Task<ExistingHabitFilter> BuildExistingHabitFilter(
        Guid userId, CancellationToken cancellationToken)
    {
        var existingHabits = await habitRepository.FindAsync(
            h => h.UserId == userId, cancellationToken);

        var recurringHabitTitles = new HashSet<string>(
            existingHabits.Where(h => h.FrequencyUnit is not null)
                .Select(h => h.Title.Trim().ToLowerInvariant()));

        var oneTimeHabitKeys = existingHabits
            .Where(h => h.FrequencyUnit is null)
            .Select(h => (
                Title: h.Title.Trim().ToLowerInvariant(),
                Date: h.DueDate.ToString("yyyy-MM-dd"),
                Time: h.DueTime?.ToString("HH:mm") ?? ""))
            .ToHashSet();

        return new ExistingHabitFilter(recurringHabitTitles, oneTimeHabitKeys);
    }

    private async Task<List<CalendarEventItem>> MapEventsToItems(
        Google.Apis.Calendar.v3.Data.Events events,
        ExistingHabitFilter filter,
        CalendarService service,
        CancellationToken cancellationToken)
    {
        var items = new List<CalendarEventItem>();
        var seenRecurringIds = new HashSet<string>();
        var seenRecurringTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var masterRRuleCache = new Dictionary<string, string?>();

        foreach (var ev in events.Items ?? [])
        {
            if (string.IsNullOrWhiteSpace(ev.Summary)) continue;

            var evTitle = ev.Summary.Trim();
            var evTitleLower = evTitle.ToLowerInvariant();
            var startDate = ev.Start?.Date ?? ev.Start?.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd");
            var startTime = ev.Start?.DateTimeDateTimeOffset?.ToString("HH:mm");

            if (IsAlreadyImported(evTitleLower, startDate, startTime, filter))
                continue;

            if (IsDuplicateRecurring(ev, evTitle, seenRecurringIds, seenRecurringTitles))
                continue;

            var endTime = ev.End?.DateTimeDateTimeOffset?.ToString("HH:mm");
            var isRecurring = ev.RecurringEventId is not null
                || (ev.Recurrence is not null && ev.Recurrence.Count > 0);
            var rrule = await ResolveRRule(ev, service, masterRRuleCache, cancellationToken);
            var reminders = BuildReminders(ev, startTime);

            items.Add(new CalendarEventItem(
                ev.Id, ev.Summary, ev.Description,
                startDate, startTime, endTime,
                isRecurring, rrule, reminders));
        }

        return items;
    }

    private static bool IsAlreadyImported(
        string titleLower, string? startDate, string? startTime,
        ExistingHabitFilter filter)
    {
        if (filter.RecurringHabitTitles.Contains(titleLower))
            return true;
        if (filter.OneTimeHabitKeys.Contains((titleLower, startDate ?? "", startTime ?? "")))
            return true;
        return false;
    }

    private static bool IsDuplicateRecurring(
        Google.Apis.Calendar.v3.Data.Event ev,
        string evTitle,
        HashSet<string> seenRecurringIds,
        HashSet<string> seenRecurringTitles)
    {
        if (ev.RecurringEventId is not null)
            return !seenRecurringIds.Add(ev.RecurringEventId) || !seenRecurringTitles.Add(evTitle);

        if (ev.Recurrence is not null && ev.Recurrence.Count > 0)
            return !seenRecurringTitles.Add(evTitle);

        return false;
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

    private static List<int> BuildReminders(
        Google.Apis.Calendar.v3.Data.Event ev, string? startTime)
    {
        var reminders = ev.Reminders?.Overrides?
            .Where(r => r.Minutes.HasValue)
            .Select(r => r.Minutes!.Value)
            .ToList() ?? [];

        // Auto-add default reminder for timed events with no explicit reminders
        if (reminders.Count == 0 && startTime is not null)
            reminders.Add(AppConstants.DefaultReminderMinutes);

        return reminders;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to fetch master event RRULE for recurring event {EventId}")]
    private static partial void LogFetchMasterRruleFailed(ILogger logger, Exception ex, string? eventId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Google Calendar API error for user {UserId}")]
    private static partial void LogGoogleCalendarApiError(ILogger logger, Exception ex, Guid userId);
}
