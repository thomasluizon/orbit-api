using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Calendar.Queries;

public record GetCalendarSyncSuggestionsQuery(Guid UserId) : IRequest<Result<List<CalendarSyncSuggestionItem>>>;

public record CalendarSyncSuggestionItem(
    Guid Id,
    string GoogleEventId,
    CalendarEventItem Event,
    DateTime DiscoveredAtUtc);

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

        var selectedCalendarIds = user.GetSelectedCalendarIds();
        var selectedCalendars = selectedCalendarIds is null
            ? null
            : new HashSet<string>(selectedCalendarIds, StringComparer.Ordinal);

        var suggestions = await suggestionRepository.FindAsync(
            s => s.UserId == request.UserId && s.DismissedAtUtc == null && s.ImportedAtUtc == null,
            cancellationToken);

        if (suggestions.Count == 0)
            return Result.Success(new List<CalendarSyncSuggestionItem>());

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var habits = await habitRepository.FindAsync(
            h => h.UserId == request.UserId,
            cancellationToken);

        var importedEventIds = habits
            .Where(h => !string.IsNullOrWhiteSpace(h.GoogleEventId))
            .Select(h => h.GoogleEventId!)
            .ToHashSet(StringComparer.Ordinal);

        var importedLegacyKeys = habits
            .Select(h => BuildLegacyMatchKey(
                h.Title,
                h.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                h.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture)))
            .ToHashSet(StringComparer.Ordinal);

        var items = new List<CalendarSyncSuggestionItem>();
        foreach (var suggestion in suggestions.OrderBy(s => s.StartDateUtc))
        {
            var item = TryBuildSuggestionItem(
                suggestion, userToday, importedEventIds, importedLegacyKeys, selectedCalendars);
            if (item is not null)
                items.Add(item);
        }

        return Result.Success(items);
    }

    private CalendarSyncSuggestionItem? TryBuildSuggestionItem(
        GoogleCalendarSyncSuggestion suggestion,
        DateOnly userToday,
        HashSet<string> importedEventIds,
        HashSet<string> importedLegacyKeys,
        HashSet<string>? selectedCalendars)
    {
        if (DateOnly.FromDateTime(suggestion.StartDateUtc) < userToday) return null;
        if (importedEventIds.Contains(suggestion.GoogleEventId)) return null;

        var eventItem = DeserializeEvent(suggestion);
        if (eventItem is null) return null;
        if (selectedCalendars is not null
            && !string.IsNullOrEmpty(eventItem.CalendarId)
            && !selectedCalendars.Contains(eventItem.CalendarId)) return null;
        if (importedLegacyKeys.Contains(BuildLegacyMatchKey(
            eventItem.Title,
            eventItem.StartDate,
            eventItem.StartTime))) return null;

        return new CalendarSyncSuggestionItem(
            suggestion.Id,
            suggestion.GoogleEventId,
            eventItem,
            suggestion.DiscoveredAtUtc);
    }

    private CalendarEventItem? DeserializeEvent(GoogleCalendarSyncSuggestion suggestion)
    {
        try
        {
            return JsonSerializer.Deserialize<CalendarEventItem>(suggestion.RawEventJson);
        }
        catch (JsonException ex)
        {
            LogDeserializeSuggestionFailed(logger, ex, suggestion.Id);
            return null;
        }
    }

    private static string BuildLegacyMatchKey(string title, string? startDate, string? startTime)
    {
        return $"{title.Trim().ToLowerInvariant()}|{startDate ?? ""}|{startTime ?? ""}";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to deserialize sync suggestion {SuggestionId}")]
    private static partial void LogDeserializeSuggestionFailed(ILogger logger, Exception ex, Guid suggestionId);
}
