using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Queries;

public record GetCalendarSyncSuggestionsQuery(Guid UserId, DateOnly? AsOf = null, DateOnly? DateTo = null,
    IReadOnlyList<string>? CalendarIds = null, bool UseCurrentSelection = true) : IRequest<Result<List<CalendarSyncSuggestionItem>>>;

public record CalendarSyncSuggestionItem(
    Guid Id,
    string GoogleEventId,
    CalendarEventItem Event,
    DateTime DiscoveredAtUtc);

public sealed record CalendarHabitMatch(string? GoogleEventId, string Title, DateOnly NextDueDate, TimeOnly? DueTime);
public sealed record CalendarSuggestionRead(Guid Id, string GoogleEventId, string RawEventJson, DateTime StartDateUtc, DateTime DiscoveredAtUtc);

public partial class GetCalendarSyncSuggestionsQueryHandler(
    IGenericRepository<GoogleCalendarSyncSuggestion> suggestionRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<User> userRepository,
    IUserDateService userDateService,
    IPayGateService payGate,
    ILogger<GetCalendarSyncSuggestionsQueryHandler> logger) : IRequestHandler<GetCalendarSyncSuggestionsQuery, Result<List<CalendarSyncSuggestionItem>>>
{
    public async Task<Result<List<CalendarSyncSuggestionItem>>> Handle(GetCalendarSyncSuggestionsQuery request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanAccessCalendar(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<List<CalendarSyncSuggestionItem>>();

        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<List<CalendarSyncSuggestionItem>>(ErrorMessages.UserNotFound);

        var selectedCalendarIds = request.UseCurrentSelection ? user.GetSelectedCalendarIds() : request.CalendarIds;
        var selectedCalendars = selectedCalendarIds is null
            ? null
            : new HashSet<string>(selectedCalendarIds, StringComparer.Ordinal);

        var suggestions = await suggestionRepository.ProjectAsync(
            s => s.UserId == request.UserId && s.DismissedAtUtc == null && s.ImportedAtUtc == null,
            q => q.Select(s => new CalendarSuggestionRead(s.Id, s.GoogleEventId, s.RawEventJson, s.StartDateUtc, s.DiscoveredAtUtc)), cancellationToken);

        if (suggestions.Count == 0)
            return Result.Success(new List<CalendarSyncSuggestionItem>());

        var userToday = request.AsOf ?? await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var habits = await habitRepository.ProjectAsync(h => h.UserId == request.UserId,
            q => q.Select(h => new CalendarHabitMatch(h.GoogleEventId, h.Title, h.NextDueDate, h.DueTime)), cancellationToken);

        var importedEventIds = habits
            .Where(h => !string.IsNullOrWhiteSpace(h.GoogleEventId))
            .Select(h => h.GoogleEventId!)
            .ToHashSet(StringComparer.Ordinal);

        var importedLegacyKeys = habits
            .Select(h => LegacyCalendarMatchKey.BuildLegacyMatchKey(
                h.Title,
                h.NextDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                h.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture)))
            .ToHashSet(StringComparer.Ordinal);

        var timeZone = TimeZoneHelper.FindTimeZone(user.TimeZone, logger, request.UserId);
        var candidates = new List<(CalendarSyncSuggestionItem Item, string LegacyKey)>();
        var malformed = 0;
        foreach (var suggestion in suggestions.OrderBy(s => s.StartDateUtc))
        {
            var item = TryBuildSuggestionItem(
                suggestion, userToday, request.DateTo, importedEventIds, selectedCalendars, timeZone, request.UserId, out var invalid);
            if (invalid) malformed++;
            if (item is not null)
            {
                candidates.Add((
                    item,
                    LegacyCalendarMatchKey.BuildLegacyMatchKey(item.Event.Title, item.Event.StartDate, item.Event.StartTime)));
            }
        }

        if (malformed > 0) LogDeserializeSuggestionsFailed(logger, request.UserId, malformed);
        var candidatesPerLegacyKey = candidates
            .GroupBy(candidate => candidate.LegacyKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var items = candidates
            .Where(candidate => candidatesPerLegacyKey[candidate.LegacyKey] > 1
                || !importedLegacyKeys.Contains(candidate.LegacyKey))
            .Select(candidate => candidate.Item)
            .ToList();

        return Result.Success(items);
    }

    private CalendarSyncSuggestionItem? TryBuildSuggestionItem(
        CalendarSuggestionRead suggestion,
        DateOnly userToday, DateOnly? dateTo,
        HashSet<string> importedEventIds,
        HashSet<string>? selectedCalendars,
        TimeZoneInfo timeZone,
        Guid userId, out bool invalid)
    {
        invalid = false;
        if (importedEventIds.Contains(suggestion.GoogleEventId)) return null;

        var sourceEvent = DeserializeEvent(suggestion);
        if (sourceEvent is null) { invalid = true; return null; }
        if (sourceEvent.HasUnrepresentableRecurrenceAfterProjection(timeZone)) return null;

        var eventItem = sourceEvent.ProjectTo(timeZone);
        var startDate = ResolveStartDate(eventItem, suggestion.StartDateUtc, timeZone);
        if (startDate < userToday || dateTo.HasValue && startDate > dateTo.Value) return null;
        if (selectedCalendars is not null
            && !string.IsNullOrEmpty(eventItem.CalendarId)
            && !selectedCalendars.Contains(eventItem.CalendarId)) return null;

        if (sourceEvent.DropsEndTimeAfterProjection(eventItem))
            LogProjectedEndTimeOmitted(logger, suggestion.GoogleEventId, userId);

        return new CalendarSyncSuggestionItem(
            suggestion.Id,
            suggestion.GoogleEventId,
            eventItem,
            suggestion.DiscoveredAtUtc);
    }

    private static DateOnly ResolveStartDate(
        CalendarEventItem eventItem,
        DateTime fallbackStartUtc,
        TimeZoneInfo timeZone)
    {
        if (DateOnly.TryParse(
                eventItem.StartDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var startDate))
        {
            return startDate;
        }

        var localStart = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(fallbackStartUtc, DateTimeKind.Utc),
            timeZone);
        return DateOnly.FromDateTime(localStart);
    }

    private static CalendarEventItem? DeserializeEvent(CalendarSuggestionRead suggestion)
    {
        try
        {
            return StoredCalendarEventJson.Deserialize(suggestion.RawEventJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to deserialize {Count} sync suggestions for user {UserId}")]
    private static partial void LogDeserializeSuggestionsFailed(ILogger logger, Guid userId, int count);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Omitted the end time of calendar event {EventId} for user {UserId}: the projected end does not follow the projected start on the projected start date")]
    private static partial void LogProjectedEndTimeOmitted(ILogger logger, string eventId, Guid userId);
}
