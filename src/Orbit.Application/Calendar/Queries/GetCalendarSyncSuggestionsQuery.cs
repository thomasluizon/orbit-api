using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
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
    ILogger<GetCalendarSyncSuggestionsQueryHandler> logger) : IRequestHandler<GetCalendarSyncSuggestionsQuery, Result<List<CalendarSyncSuggestionItem>>>
{
    public async Task<Result<List<CalendarSyncSuggestionItem>>> Handle(GetCalendarSyncSuggestionsQuery request, CancellationToken cancellationToken)
    {
        var suggestions = await suggestionRepository.FindAsync(
            s => s.UserId == request.UserId && s.DismissedAtUtc == null && s.ImportedAtUtc == null,
            cancellationToken);

        var items = new List<CalendarSyncSuggestionItem>();
        foreach (var suggestion in suggestions.OrderBy(s => s.StartDateUtc))
        {
            var eventItem = DeserializeEvent(suggestion);
            if (eventItem is null) continue;
            items.Add(new CalendarSyncSuggestionItem(
                suggestion.Id,
                suggestion.GoogleEventId,
                eventItem,
                suggestion.DiscoveredAtUtc));
        }

        return Result.Success(items);
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to deserialize sync suggestion {SuggestionId}")]
    private static partial void LogDeserializeSuggestionFailed(ILogger logger, Exception ex, Guid suggestionId);
}
