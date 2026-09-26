using System.Text.Json;
using System.Text.Json.Nodes;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar;

internal static class StoredCalendarEventJson
{
    private const string SourceTimeZoneKey = nameof(CalendarEventItem.SourceTimeZone);
    private const string RecurrenceStartUtcKey = nameof(CalendarEventItem.RecurrenceStartUtc);
    private const string ExpandedOccurrencesUtcKey = nameof(CalendarEventItem.ExpandedOccurrencesUtc);

    internal static string Serialize(CalendarEventItem item)
    {
        var stored = JsonSerializer.SerializeToNode(item)!.AsObject();
        stored[SourceTimeZoneKey] = item.SourceTimeZone;
        stored[RecurrenceStartUtcKey] = item.RecurrenceStartUtc;
        stored[ExpandedOccurrencesUtcKey] = JsonSerializer.SerializeToNode(item.ExpandedOccurrencesUtc);
        return stored.ToJsonString();
    }

    internal static CalendarEventItem? Deserialize(string rawEventJson)
    {
        if (JsonNode.Parse(rawEventJson) is not JsonObject stored)
            return null;

        var item = stored.Deserialize<CalendarEventItem>();
        if (item is null)
            return null;

        var sourceTimeZone = ReadSourceTimeZone(stored);
        return item with
        {
            SourceTimeZone = sourceTimeZone,
            RecurrenceStartUtc = ReadRecurrenceStartUtc(stored),
            ExpandedOccurrencesUtc = ReadExpandedOccurrencesUtc(stored),
            RecurrenceTimeZone = item.IsRecurring && item.StartTime is not null
                && !string.IsNullOrWhiteSpace(sourceTimeZone)
                ? sourceTimeZone
                : null
        };
    }

    internal static bool NeedsRecurrenceEvidenceRefresh(string rawEventJson)
    {
        try
        {
            return Deserialize(rawEventJson)?.NeedsRecurrenceEvidenceRefresh ?? true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static string? ReadSourceTimeZone(JsonObject stored)
        => stored[SourceTimeZoneKey] is JsonValue value && value.TryGetValue<string>(out var timeZoneId)
            ? timeZoneId
            : null;

    private static DateTime? ReadRecurrenceStartUtc(JsonObject stored)
        => stored[RecurrenceStartUtcKey] is JsonValue value
            && value.TryGetValue<DateTime>(out var startUtc)
            ? startUtc
            : null;

    private static IReadOnlyList<DateTime>? ReadExpandedOccurrencesUtc(JsonObject stored)
    {
        try
        {
            return stored[ExpandedOccurrencesUtcKey]?.Deserialize<List<DateTime>>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
