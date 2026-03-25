using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using MediatR;
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

public class GetCalendarEventsQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGoogleTokenService googleTokenService,
    IUnitOfWork unitOfWork) : IRequestHandler<GetCalendarEventsQuery, Result<List<CalendarEventItem>>>
{
    public async Task<Result<List<CalendarEventItem>>> Handle(GetCalendarEventsQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<List<CalendarEventItem>>(ErrorMessages.UserNotFound);

        var accessToken = await googleTokenService.GetValidAccessTokenAsync(user, cancellationToken);
        if (accessToken is null)
            return Result.Failure<List<CalendarEventItem>>("Google Calendar not connected. Please sign in with Google first.");

        await unitOfWork.SaveChangesAsync(cancellationToken); // Persist refreshed token

        try
        {
            var credential = Google.Apis.Auth.OAuth2.GoogleCredential.FromAccessToken(accessToken);
            var service = new CalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Orbit"
            });

            var listRequest = service.Events.List("primary");
            listRequest.SingleEvents = true;
            listRequest.TimeMinDateTimeOffset = DateTimeOffset.UtcNow;
            listRequest.TimeMaxDateTimeOffset = DateTimeOffset.UtcNow.AddDays(60);
            listRequest.MaxResults = 250;
            listRequest.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

            var events = await listRequest.ExecuteAsync(cancellationToken);

            // Load existing habits to hide already-imported events (by title + date)
            var existingHabits = await habitRepository.FindAsync(
                h => h.UserId == request.UserId, cancellationToken);
            // Recurring habits: match by title only (DueDate advances, so exact date won't match)
            var recurringHabitTitles = new HashSet<string>(
                existingHabits.Where(h => h.FrequencyUnit is not null).Select(h => h.Title.Trim().ToLowerInvariant()));
            // One-time habits: match by title + date + time
            var oneTimeHabitKeys = existingHabits
                .Where(h => h.FrequencyUnit is null)
                .Select(h => (Title: h.Title.Trim().ToLowerInvariant(), Date: h.DueDate.ToString("yyyy-MM-dd"), Time: h.DueTime?.ToString("HH:mm") ?? ""))
                .ToHashSet();

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

                // Skip if matching an existing recurring habit (title only)
                if (recurringHabitTitles.Contains(evTitleLower)) continue;
                // Skip if matching an existing one-time habit (title + date + time)
                if (oneTimeHabitKeys.Contains((evTitleLower, startDate ?? "", startTime ?? ""))) continue;

                // Deduplicate recurring instances: by RecurringEventId and by title
                // (master event may have null RecurringEventId while instances have non-null)
                if (ev.RecurringEventId is not null)
                {
                    if (!seenRecurringIds.Add(ev.RecurringEventId)) continue;
                    if (!seenRecurringTitles.Add(evTitle)) continue;
                }
                else if (ev.Recurrence is not null && ev.Recurrence.Count > 0)
                {
                    // Master recurring event (has Recurrence rules but no RecurringEventId)
                    if (!seenRecurringTitles.Add(evTitle)) continue;
                }

                var endTime = ev.End?.DateTimeDateTimeOffset?.ToString("HH:mm");
                var isRecurring = ev.RecurringEventId is not null
                    || (ev.Recurrence is not null && ev.Recurrence.Count > 0);

                // Get RRULE: from own Recurrence (master event) or fetch from master for instances
                string? rrule = null;
                if (ev.Recurrence is not null)
                {
                    rrule = ev.Recurrence.FirstOrDefault(r => r.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));
                }
                else if (ev.RecurringEventId is not null)
                {
                    if (!masterRRuleCache.TryGetValue(ev.RecurringEventId, out rrule))
                    {
                        try
                        {
                            var master = await service.Events.Get("primary", ev.RecurringEventId).ExecuteAsync(cancellationToken);
                            rrule = master.Recurrence?.FirstOrDefault(r => r.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));
                        }
                        catch { rrule = null; }
                        masterRRuleCache[ev.RecurringEventId] = rrule;
                    }
                }

                var reminders = new List<int>();
                if (ev.Reminders?.Overrides is not null)
                {
                    foreach (var r in ev.Reminders.Overrides)
                    {
                        if (r.Minutes.HasValue)
                            reminders.Add(r.Minutes.Value);
                    }
                }

                items.Add(new CalendarEventItem(
                    ev.Id,
                    ev.Summary,
                    ev.Description,
                    startDate,
                    startTime,
                    endTime,
                    isRecurring,
                    rrule,
                    reminders));
            }

            return Result.Success(items);
        }
        catch (Google.GoogleApiException ex)
        {
            return Result.Failure<List<CalendarEventItem>>($"Google Calendar API error: {ex.Message}");
        }
    }
}
