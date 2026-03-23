using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using MediatR;
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
            return Result.Failure<List<CalendarEventItem>>("User not found");

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

            // Load existing habit titles to hide already-imported events
            var existingHabits = await habitRepository.FindAsync(
                h => h.UserId == request.UserId, cancellationToken);
            var existingTitles = new HashSet<string>(
                existingHabits.Select(h => h.Title.Trim()), StringComparer.OrdinalIgnoreCase);

            var items = new List<CalendarEventItem>();
            var seenTitles = new HashSet<string>(existingTitles, StringComparer.OrdinalIgnoreCase);
            foreach (var ev in events.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(ev.Summary)) continue;
                var evTitle = ev.Summary.Trim();
                if (existingTitles.Contains(evTitle)) continue;

                // Deduplicate by title (covers recurring instances + already-imported)
                if (!seenTitles.Add(evTitle)) continue;

                var startDate = ev.Start?.Date ?? ev.Start?.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd");
                var startTime = ev.Start?.DateTimeDateTimeOffset?.ToString("HH:mm");
                var endTime = ev.End?.DateTimeDateTimeOffset?.ToString("HH:mm");
                var isRecurring = ev.RecurringEventId is not null;
                var rrule = ev.Recurrence?.FirstOrDefault(r => r.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase));

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
